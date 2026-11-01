using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 粒子光束系统 (Particle Beam System)
    // --------------------------------------------------------------------
    // 设计目标：对标 UE Beam Emitter / Lightning 系统的完整光束粒子效果
    //   - BeamEmissionMode：Trail / Target / Branch / Decal
    //   - BeamSource：起点/方向/速度
    //   - BeamTarget：终点（固定/追踪/附加）
    //   - BeamParam：宽度/颜色/不透明度/噪声/扭曲/速度/碰撞
    //   - BeamBranch：分支规则（角度/概率/深度/子束数）
    //   - BeamTrail：拖尾（历史点数组/颜色衰减/宽度衰减）
    //   - Lightning 效果：分叉闪电/随机偏移/亮度闪烁
    //   - Chain Lightning：链状闪电（多目标链接）
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：光束发射模式
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束发射模式。
    /// </summary>
    public enum BeamEmissionMode
    {
        /// <summary>拖尾模式：光束从源沿方向延伸，基于运动历史。</summary>
        Trail,
        /// <summary>目标模式：光束从源到目标直线延伸。</summary>
        Target,
        /// <summary>分支模式：光束在中途分叉为多条子光束。</summary>
        Branch,
        /// <summary>贴花模式：光束在碰撞表面留下投影贴花。</summary>
        Decal
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：光束源与目标
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束源描述。定义光束的起点位置、初始方向和发射速度。
    /// </summary>
    public struct BeamSource
    {
        /// <summary>起点位置（世界空间）。</summary>
        public Vector3 Position;
        /// <summary>初始方向（归一化）。</summary>
        public Vector3 Direction;
        /// <summary>发射速度（影响光束延伸速度）。</summary>
        public float Speed;
        /// <summary>位置偏移随机范围。</summary>
        public float PositionSpread;
        /// <summary>方向偏移随机角度（弧度）。</summary>
        public float DirectionSpread;

        public static BeamSource Default => new()
        {
            Position = Vector3.Zero,
            Direction = Vector3.UnitY,
            Speed = 10f,
            PositionSpread = 0.1f,
            DirectionSpread = 0.05f
        };
    }

    /// <summary>
    /// 光束目标类型。
    /// </summary>
    public enum BeamTargetType
    {
        /// <summary>固定位置。</summary>
        Fixed,
        /// <summary>追踪目标（随时间移动）。</summary>
        Tracking,
        /// <summary>附加到骨骼/实体。</summary>
        Attached
    }

    /// <summary>
    /// 光束目标描述。定义光束的终点。
    /// </summary>
    public class BeamTarget
    {
        /// <summary>目标类型。</summary>
        public BeamTargetType TargetType = BeamTargetType.Fixed;
        /// <summary>固定位置。</summary>
        public Vector3 FixedPosition = new(0, 5, 0);
        /// <summary>追踪目标实体ID（预留）。</summary>
        public int TrackingEntityId = -1;
        /// <summary>追踪速度（多快靠近目标）。</summary>
        public float TrackingSpeed = 5f;
        /// <summary>附加骨骼名称。</summary>
        public string AttachedBoneName = "";
        /// <summary>目标到达后是否保持。</summary>
        public bool LockOnTarget = true;

        /// <summary>获取当前目标位置。</summary>
        public Vector3 GetCurrentPosition(float deltaTime)
        {
            // 在完整引擎中此处查询实体位置
            return FixedPosition;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：光束参数
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束参数。控制光束的外观和物理行为。
    /// </summary>
    public class BeamParam
    {
        /// <summary>光束基础宽度。</summary>
        public float Width = 0.05f;
        /// <summary>宽度随长度的衰减曲线（0=无衰减，1=线性）。</summary>
        public float WidthTaper = 0.5f;
        /// <summary>光束颜色。</summary>
        public Vector4 Color = new(0.6f, 0.8f, 1f, 1f);
        /// <summary>不透明度。</summary>
        public float Opacity = 1f;
        /// <summary>噪声强度（控制光束抖动）。</summary>
        public float NoiseStrength = 0.3f;
        /// <summary>噪声频率。</summary>
        public float NoiseFrequency = 5f;
        /// <summary>噪声速度（噪声沿光束流动的速度）。</summary>
        public float NoiseSpeed = 2f;
        /// <summary>扭曲强度。</summary>
        public float DistortionStrength;
        /// <summary>扭曲频率。</summary>
        public float DistortionFrequency = 3f;
        /// <summary>光束存活时间（0=无限）。</summary>
        public float Lifetime = 0.5f;
        /// <summary>光束分段数。</summary>
        public int Segments = 16;
        /// <summary>是否启用碰撞。</summary>
        public bool CollisionEnabled;
        /// <summary>碰撞弹性。</summary>
        public float CollisionBounce = 0.3f;
        /// <summary>光束材质。</summary>
        public string MaterialName = "DefaultBeam";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：光束分支
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束分支规则。控制光束在何处、如何分叉为子光束。
    /// </summary>
    public class BeamBranch
    {
        /// <summary>是否启用分支。</summary>
        public bool Enabled;
        /// <summary>分支角度范围（弧度）。</summary>
        public float AngleRange = 0.5f;
        /// <summary>每个分段产生分支的概率 [0,1]。</summary>
        public float Probability = 0.3f;
        /// <summary>最大分支深度（0=仅一层分支）。</summary>
        public int MaxDepth = 2;
        /// <summary>每次分支产生的子束数量。</summary>
        public int BranchesPerSplit = 2;
        /// <summary>子束宽度衰减。</summary>
        public float WidthDecay = 0.6f;
        /// <summary>子束不透明度衰减。</summary>
        public float OpacityDecay = 0.7f;
        /// <summary>子束长度衰减。</summary>
        public float LengthDecay = 0.5f;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：光束拖尾
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束拖尾数据。保存光束历史点，用于渲染渐变拖尾效果。
    /// </summary>
    public class BeamTrail
    {
        /// <summary>历史点数组。</summary>
        public readonly List<BeamTrailPoint> Points = new();
        /// <summary>最大历史点数。</summary>
        public int MaxPoints = 32;
        /// <summary>颜色随年龄衰减速率。</summary>
        public float ColorDecayRate = 2f;
        /// <summary>宽度随年龄衰减速率。</summary>
        public float WidthDecayRate = 1.5f;
        /// <summary>点间距（世界单位）。</summary>
        public float PointSpacing = 0.1f;

        /// <summary>添加拖尾点。</summary>
        public void AddPoint(Vector3 position, Vector4 color, float width)
        {
            Points.Add(new BeamTrailPoint
            {
                Position = position,
                Color = color,
                Width = width,
                Age = 0f
            });
            while (Points.Count > MaxPoints)
                Points.RemoveAt(0);
        }

        /// <summary>更新拖尾年龄。</summary>
        public void Update(float dt)
        {
            for (int i = Points.Count - 1; i >= 0; i--)
            {
                var point = Points[i];
                point.Age += dt;
                // 衰减颜色
                float fade = MathF.Max(0f, 1f - point.Age * ColorDecayRate);
                point.Color.W *= fade;
                // 衰减宽度
                point.Width *= MathF.Max(0f, 1f - point.Age * WidthDecayRate * dt);
                Points[i] = point;

                if (Points[i].Color.W < 0.01f || Points[i].Width < 0.001f)
                {
                    Points.RemoveAt(i);
                }
            }
        }

        /// <summary>清空拖尾。</summary>
        public void Clear() => Points.Clear();
    }

    /// <summary>
    /// 光束拖尾点。
    /// </summary>
    public struct BeamTrailPoint
    {
        public Vector3 Position;
        public Vector4 Color;
        public float Width;
        public float Age;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：闪电效果
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 闪电参数。控制分叉闪电的视觉效果。
    /// </summary>
    public class LightningParams
    {
        /// <summary>是否启用闪电模式。</summary>
        public bool Enabled;
        /// <summary>分支数量。</summary>
        public int BranchCount = 5;
        /// <summary>随机偏移强度（控制闪电锯齿程度）。</summary>
        public float Jaggedness = 0.5f;
        /// <summary>随机偏移频率。</summary>
        public float Detail = 4f;
        /// <summary>亮度闪烁速度。</summary>
        public float FlickerSpeed = 10f;
        /// <summary>亮度闪烁强度。</summary>
        public float FlickerIntensity = 0.3f;
        /// <summary>闪电颜色。</summary>
        public Vector4 Color = new(0.7f, 0.8f, 1f, 1f);
        /// <summary>闪电宽度。</summary>
        public float Width = 0.03f;
        /// <summary>随机种子。</summary>
        public int Seed;
        /// <summary>分叉深度。</summary>
        public int ForkDepth = 2;
    }

    /// <summary>
    /// 链状闪电参数。多目标链接的闪电效果。
    /// </summary>
    public class ChainLightningParams
    {
        /// <summary>是否启用链状闪电。</summary>
        public bool Enabled;
        /// <summary>目标点列表。</summary>
        public readonly List<Vector3> Targets = new();
        /// <summary>链接最大距离。</summary>
        public float MaxLinkDistance = 10f;
        /// <summary>链接衰减（距离越远越暗）。</summary>
        public float DistanceFalloff = 0.5f;
        /// <summary>链接动画速度。</summary>
        public float AnimationSpeed = 3f;
        /// <summary>链接宽度。</summary>
        public float LinkWidth = 0.02f;
        /// <summary>链接颜色。</summary>
        public Vector4 LinkColor = new(0.5f, 0.6f, 1f, 0.8f);
        /// <summary>每个链接的分支数。</summary>
        public int LinksPerTarget = 2;
        /// <summary>链传播延迟（秒，每个链接的延迟）。</summary>
        public float PropagationDelay = 0.05f;

        /// <summary>添加目标。</summary>
        public void AddTarget(Vector3 position) => Targets.Add(position);
        /// <summary>清空目标。</summary>
        public void ClearTargets() => Targets.Clear();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：光束段数据（渲染用）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束段。渲染管线使用的数据结构。
    /// </summary>
    public struct BeamSegment
    {
        /// <summary>起点位置。</summary>
        public Vector3 Start;
        /// <summary>终点位置。</summary>
        public Vector3 End;
        /// <summary>起点宽度。</summary>
        public float StartWidth;
        /// <summary>终点宽度。</summary>
        public float EndWidth;
        /// <summary>起点颜色。</summary>
        public Vector4 StartColor;
        /// <summary>终点颜色。</summary>
        public Vector4 EndColor;
        /// <summary>纹理U坐标起点。</summary>
        public float UVStart;
        /// <summary>纹理U坐标终点。</summary>
        public float UVEnd;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：光束发射器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子光束发射器。生成和管理光束效果。
    /// </summary>
    public class BeamEmitter
    {
        /// <summary>发射器名称。</summary>
        public string Name = "BeamEmitter";
        /// <summary>是否启用。</summary>
        public bool Enabled = true;
        /// <summary>是否正在播放。</summary>
        public bool IsPlaying;

        /// <summary>发射模式。</summary>
        public BeamEmissionMode EmissionMode = BeamEmissionMode.Target;

        /// <summary>光束源。</summary>
        public BeamSource Source = BeamSource.Default;

        /// <summary>光束目标。</summary>
        public BeamTarget Target = new();

        /// <summary>光束参数。</summary>
        public readonly BeamParam Param = new();

        /// <summary>分支规则。</summary>
        public readonly BeamBranch Branch = new();

        /// <summary>拖尾。</summary>
        public readonly BeamTrail Trail = new();

        /// <summary>闪电参数。</summary>
        public readonly LightningParams Lightning = new();

        /// <summary>链状闪电参数。</summary>
        public readonly ChainLightningParams ChainLightning = new();

        /// <summary>当前生成的光束段（供渲染使用）。</summary>
        public readonly List<BeamSegment> Segments = new();

        private float _elapsedTime;
        private float _beamAge;
        private readonly Random _rng = new();

        /// <summary>光束当前年龄。</summary>
        public float BeamAge => _beamAge;

        /// <summary>开始播放。</summary>
        public void Play()
        {
            IsPlaying = true;
            _elapsedTime = 0f;
            _beamAge = 0f;
            Segments.Clear();
            Trail.Clear();
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            IsPlaying = false;
            Segments.Clear();
            Trail.Clear();
        }

        /// <summary>暂停。</summary>
        public void Pause() => IsPlaying = false;

        /// <summary>恢复。</summary>
        public void Resume() => IsPlaying = true;

        /// <summary>每帧更新光束。</summary>
        public void Update(float dt)
        {
            if (!Enabled || !IsPlaying) return;
            _elapsedTime += dt;
            _beamAge += dt;

            // 更新拖尾
            Trail.Update(dt);

            // 生成光束段
            Segments.Clear();
            switch (EmissionMode)
            {
                case BeamEmissionMode.Target:
                    GenerateTargetBeam(dt);
                    break;
                case BeamEmissionMode.Trail:
                    GenerateTrailBeam(dt);
                    break;
                case BeamEmissionMode.Branch:
                    GenerateBranchBeam(dt);
                    break;
            }

            // 闪电效果叠加
            if (Lightning.Enabled)
                GenerateLightning(dt);

            // 链状闪电
            if (ChainLightning.Enabled)
                GenerateChainLightning(dt);

            // 生命周期
            if (Param.Lifetime > 0f && _beamAge > Param.Lifetime)
            {
                Stop();
            }
        }

        // ── 目标模式 ──

        private void GenerateTargetBeam(float dt)
        {
            Vector3 startPos = Source.Position + RandomOffset(Source.PositionSpread);
            Vector3 endPos = Target.GetCurrentPosition(dt);

            // 生成光束分段
            GenerateBeamSegments(startPos, endPos, Param, 0, Vector4.One);
        }

        // ── 拖尾模式 ──

        private void GenerateTrailBeam(float dt)
        {
            Vector3 currentPos = Source.Position + Source.Direction * Source.Speed * _elapsedTime;
            currentPos += RandomOffset(Source.PositionSpread * 0.1f);

            Trail.AddPoint(currentPos, Param.Color, Param.Width);

            // 从拖尾点生成光束段
            if (Trail.Points.Count >= 2)
            {
                for (int i = 0; i < Trail.Points.Count - 1; i++)
                {
                    var p0 = Trail.Points[i];
                    var p1 = Trail.Points[i + 1];
                    Segments.Add(new BeamSegment
                    {
                        Start = p0.Position,
                        End = p1.Position,
                        StartWidth = p0.Width,
                        EndWidth = p1.Width,
                        StartColor = p0.Color,
                        EndColor = p1.Color,
                        UVStart = (float)i / Trail.Points.Count,
                        UVEnd = (float)(i + 1) / Trail.Points.Count
                    });
                }
            }
        }

        // ── 分支模式 ──

        private void GenerateBranchBeam(float dt)
        {
            Vector3 startPos = Source.Position + RandomOffset(Source.PositionSpread);
            Vector3 endPos = Target.GetCurrentPosition(dt);
            GenerateBeamWithBranching(startPos, endPos, Param, Branch, 0);
        }

        /// <summary>生成带分支的光束段（递归）。</summary>
        private void GenerateBeamWithBranching(Vector3 start, Vector3 end, BeamParam param, BeamBranch branch, int depth)
        {
            if (depth > branch.MaxDepth) return;

            GenerateBeamSegments(start, end, param, depth, Vector4.One);

            if (!branch.Enabled || depth >= branch.MaxDepth) return;

            Vector3 dir = end - start;
            float length = dir.Length();
            if (length < 0.01f) return;
            dir /= length;

            // 沿光束路径产生分支
            int branchAttempts = Math.Max(1, (int)(length * 2f));
            for (int i = 0; i < branchAttempts; i++)
            {
                if ((float)_rng.NextDouble() > branch.Probability) continue;

                float t = (float)_rng.NextDouble();
                Vector3 branchPoint = start + dir * length * t;

                // 计算分支方向
                float angle = ((float)_rng.NextDouble() - 0.5f) * branch.AngleRange * 2f;
                Quaternion rot = Quaternion.CreateFromAxisAngle(
                    Vector3.Normalize(new Vector3((float)_rng.NextDouble() - 0.5f,
                        (float)_rng.NextDouble() - 0.5f,
                        (float)_rng.NextDouble() - 0.5f)), angle);
                Vector3 branchDir = Vector3.Transform(dir, rot);

                float branchLength = length * branch.LengthDecay;
                Vector3 branchEnd = branchPoint + branchDir * branchLength;

                // 子束参数衰减
                var subParam = new BeamParam
                {
                    Width = param.Width * branch.WidthDecay,
                    Color = new Vector4(param.Color.X, param.Color.Y, param.Color.Z,
                        param.Color.W * branch.OpacityDecay),
                    Segments = Math.Max(4, param.Segments / 2),
                    NoiseStrength = param.NoiseStrength * 1.2f,
                    NoiseFrequency = param.NoiseFrequency
                };

                GenerateBeamWithBranching(branchPoint, branchEnd, subParam, branch, depth + 1);
            }
        }

        // ── 基础光束段生成 ──

        /// <summary>生成从 start 到 end 的光束分段（含噪声偏移）。</summary>
        private void GenerateBeamSegments(Vector3 start, Vector3 end, BeamParam param, int depth, Vector4 colorMul)
        {
            Vector3 dir = end - start;
            float length = dir.Length();
            if (length < 0.001f) return;
            Vector3 normDir = dir / length;

            // 计算垂直方向（用于噪声偏移）
            Vector3 up = MathF.Abs(normDir.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 right = Vector3.Cross(normDir, up);
            right = Vector3.Normalize(right);
            Vector3 actualUp = Vector3.Cross(right, normDir);

            int segments = Math.Max(2, param.Segments);
            float segLength = length / segments;

            Vector3 prevPoint = start;
            float prevWidth = param.Width;
            Vector4 prevColor = param.Color * colorMul;

            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector3 point = start + dir * t;

                // 噪声偏移
                if (param.NoiseStrength > 0f)
                {
                    float noise = SimpleNoise(t * param.NoiseFrequency + _elapsedTime * param.NoiseSpeed + depth * 17.3f);
                    float distortion = 0f;
                    if (param.DistortionStrength > 0f)
                        distortion = MathF.Sin(t * param.DistortionFrequency * MathF.PI * 2f + _elapsedTime * 3f) * param.DistortionStrength;
                    float offset = (noise + distortion) * param.NoiseStrength;
                    point += right * offset;
                }

                // 宽度衰减（沿长度逐渐变细）
                float width = param.Width * (1f - t * param.WidthTaper);

                // 颜色
                Vector4 color = param.Color * colorMul;
                color.W *= param.Opacity;

                Segments.Add(new BeamSegment
                {
                    Start = prevPoint,
                    End = point,
                    StartWidth = prevWidth,
                    EndWidth = width,
                    StartColor = prevColor,
                    EndColor = color,
                    UVStart = (i - 1f) / segments,
                    UVEnd = t
                });

                prevPoint = point;
                prevWidth = width;
                prevColor = color;
            }
        }

        // ── 闪电效果 ──

        private void GenerateLightning(float dt)
        {
            if (!Lightning.Enabled) return;
            Vector3 start = Source.Position;
            Vector3 end = Target.GetCurrentPosition(dt);

            // 亮度闪烁
            float flicker = 1f + MathF.Sin(_elapsedTime * Lightning.FlickerSpeed) * Lightning.FlickerIntensity
                           + (float)_rng.NextDouble() * Lightning.FlickerIntensity * 0.5f;

            Vector4 boltColor = new(
                Lightning.Color.X * flicker,
                Lightning.Color.Y * flicker,
                Lightning.Color.Z * flicker,
                Lightning.Color.W);

            // 生成主闪电
            GenerateLightningBolt(start, end, Lightning, 0, boltColor);

            // 分叉闪电
            if (Lightning.ForkDepth > 0)
                GenerateLightningForks(start, end, Lightning, 1);
        }

        /// <summary>生成单条闪电（锯齿状）。</summary>
        private void GenerateLightningBolt(Vector3 start, Vector3 end, LightningParams lp, int seed, Vector4 color)
        {
            Vector3 dir = end - start;
            float length = dir.Length();
            Vector3 normDir = length > 0.001f ? dir / length : Vector3.UnitY;

            Vector3 up = MathF.Abs(normDir.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 right = Vector3.Cross(normDir, up);
            right = Vector3.Normalize(right);

            int segments = Math.Max(4, lp.BranchCount * 4);
            float segLength = length / segments;

            var rng = new Random(lp.Seed + seed);
            Vector3 prev = start;

            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector3 point = start + dir * t;

                // 锯齿偏移（中段最大，两端最小）
                float envelope = MathF.Sin(t * MathF.PI);
                float offset = ((float)rng.NextDouble() - 0.5f) * 2f * lp.Jaggedness * envelope;
                point += right * offset;

                float width = lp.Width * (1f - t * 0.5f);

                Segments.Add(new BeamSegment
                {
                    Start = prev,
                    End = point,
                    StartWidth = width,
                    EndWidth = width * 0.8f,
                    StartColor = color,
                    EndColor = color * new Vector4(1, 1, 1, 1f - t * 0.3f),
                    UVStart = (i - 1f) / segments,
                    UVEnd = t
                });

                prev = point;
            }
        }

        /// <summary>生成分叉闪电（递归）。</summary>
        private void GenerateLightningForks(Vector3 start, Vector3 end, LightningParams lp, int depth)
        {
            if (depth > lp.ForkDepth) return;

            Vector3 dir = end - start;
            float length = dir.Length();

            int forkCount = lp.BranchCount / 2 + 1;
            for (int f = 0; f < forkCount; f++)
            {
                float t = 0.2f + 0.6f * (float)_rng.NextDouble();
                Vector3 forkStart = start + dir * t;

                float angle = ((float)_rng.NextDouble() - 0.5f) * MathF.PI * 0.5f;
                Vector3 forkDir = Vector3.Transform(dir, Quaternion.CreateFromYawPitchRoll(angle, 0, 0));
                forkDir = Vector3.Normalize(forkDir);

                float forkLength = length * (1f - t) * 0.5f * (float)_rng.NextDouble();
                Vector3 forkEnd = forkStart + forkDir * forkLength;

                var forkParams = new LightningParams
                {
                    Enabled = true,
                    BranchCount = Math.Max(2, lp.BranchCount / 2),
                    Jaggedness = lp.Jaggedness * 0.7f,
                    Color = lp.Color * new Vector4(1, 1, 1, 0.6f),
                    Width = lp.Width * 0.5f,
                    Seed = lp.Seed + depth * 100 + f * 37
                };

                GenerateLightningBolt(forkStart, forkEnd, forkParams, depth * 100 + f, forkParams.Color);
                GenerateLightningForks(forkStart, forkEnd, forkParams, depth + 1);
            }
        }

        // ── 链状闪电 ──

        private void GenerateChainLightning(float dt)
        {
            if (!ChainLightning.Enabled || ChainLightning.Targets.Count == 0) return;

            Vector3 source = Source.Position;
            float cumulativeDelay = 0f;

            foreach (var target in ChainLightning.Targets)
            {
                float dist = Vector3.Distance(source, target);
                if (dist > ChainLightning.MaxLinkDistance) continue;

                // 检查是否已到达传播延迟
                cumulativeDelay += ChainLightning.PropagationDelay;
                float linkAge = _beamAge - cumulativeDelay;
                if (linkAge < 0f) continue;

                // 距离衰减
                float falloff = 1f - (dist / ChainLightning.MaxLinkDistance) * ChainLightning.DistanceFalloff;
                falloff = Math.Clamp(falloff, 0f, 1f);

                Vector4 linkCol = new(
                    ChainLightning.LinkColor.X,
                    ChainLightning.LinkColor.Y,
                    ChainLightning.LinkColor.Z,
                    ChainLightning.LinkColor.W * falloff);

                // 生成链闪电段
                var linkLightning = new LightningParams
                {
                    Enabled = true,
                    BranchCount = 4,
                    Jaggedness = 0.3f,
                    Color = linkCol,
                    Width = ChainLightning.LinkWidth * falloff,
                    Seed = (int)(dist * 100)
                };

                GenerateLightningBolt(source, target, linkLightning, (int)(dist * 10), linkCol);

                // 每个目标产生额外的分支链接
                for (int b = 0; b < ChainLightning.LinksPerTarget; b++)
                {
                    Vector3 branchEnd = target + new Vector3(
                        (float)_rng.NextDouble() - 0.5f,
                        (float)_rng.NextDouble() - 0.5f,
                        (float)_rng.NextDouble() - 0.5f) * 2f;

                    var branchLightning = new LightningParams
                    {
                        Enabled = true,
                        BranchCount = 2,
                        Jaggedness = 0.4f,
                        Color = linkCol * new Vector4(1, 1, 1, 0.5f),
                        Width = ChainLightning.LinkWidth * falloff * 0.5f,
                        Seed = (int)(dist * 100) + b * 53
                    };

                    GenerateLightningBolt(target, branchEnd, branchLightning, (int)(dist * 10) + b, branchLightning.Color);
                }

                source = target;
            }
        }

        // ── 辅助 ──

        private Vector3 RandomOffset(float spread)
        {
            return new Vector3(
                ((float)_rng.NextDouble() - 0.5f) * 2f * spread,
                ((float)_rng.NextDouble() - 0.5f) * 2f * spread,
                ((float)_rng.NextDouble() - 0.5f) * 2f * spread);
        }

        private static float SimpleNoise(float x)
        {
            float s = MathF.Sin(x * 12.9898f) * 43758.5453f;
            return s - MathF.Floor(s);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第九部分：光束系统管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 光束系统管理器。管理多个光束发射器的生命周期。
    /// </summary>
    public class BeamSystem
    {
        /// <summary>所有活跃的光束发射器。</summary>
        public readonly List<BeamEmitter> Emitters = new();

        /// <summary>添加光束发射器。</summary>
        public BeamEmitter AddEmitter(BeamEmitter emitter)
        {
            Emitters.Add(emitter);
            return emitter;
        }

        /// <summary>移除光束发射器。</summary>
        public bool RemoveEmitter(BeamEmitter emitter) => Emitters.Remove(emitter);

        /// <summary>每帧更新所有光束。</summary>
        public void Update(float dt)
        {
            for (int i = Emitters.Count - 1; i >= 0; i--)
            {
                Emitters[i].Update(dt);
                if (!Emitters[i].IsPlaying && Emitters[i].Segments.Count == 0)
                {
                    // 可选：自动清理已停止的发射器
                    // Emitters.RemoveAt(i);
                }
            }
        }

        /// <summary>获取全部光束段（合并所有发射器的段）。</summary>
        public List<BeamSegment> GetAllSegments()
        {
            var all = new List<BeamSegment>();
            foreach (var emitter in Emitters)
                all.AddRange(emitter.Segments);
            return all;
        }

        /// <summary>总段数。</summary>
        public int TotalSegments
        {
            get
            {
                int c = 0;
                foreach (var e in Emitters) c += e.Segments.Count;
                return c;
            }
        }
    }
}
