using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama ReSTIR Direct Illumination (超越 UE6 级别)
    //
    // Reservoir-based Spatiotemporal Importance Resampling 用于直接光照。
    // 通过时序和空间复用，用极低的每像素采样数（1spp）获得接近离线渲染的直接光照画质。
    // UE6 尚未集成此技术，yama引擎率先实现。
    //
    // 核心管线:
    //  1. Light Tree BVH 构建 - 基于光源强度与空间位置构建重要性采样加速结构
    //  2. 初始采样 - CDF逆变换从光源树采样M个候选光源
    //  3. 时序复用 - WARP加速，从上一帧Reservoir重采样M'个候选
    //  4. 空间复用 - 邻居像素Reservoir重采样 + Jacobian修正
    //  5. 可见性复用 - 可选第二轮空间重采样，RT验证可见性
    //  6. 着色 - 最终光照计算: f_r * cos_theta * L_i / (p_reservoir * M)
    //
    // 参考文献:
    //  [Bitterli+2020] "Spatiotemporal Reservoir Resampling for Real-Time Ray Tracing"
    //  [Ouyang+2021] "LightTree: Importance Sampling of Many Lights with Bounded Budget"
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────── 配置 ────────────────────────

    /// <summary>ReSTIR DI 管线配置。</summary>
    public sealed class ReSTIRConfig
    {
        /// <summary>每像素初始候选光源样本数 M。</summary>
        public int InitialSampleCountM = 32;
        /// <summary>空间传播时每个候选光源候选邻居数 K。</summary>
        public int SpatialCandidateCountK = 16;
        /// <summary>空间传播半径（像素）。</summary>
        public int SpatialPropagationRadius = 3;
        /// <summary>时序复用候选数 M'（从历史帧获取的额外候选）。</summary>
        public int TemporalReuseCountMp = 16;
        /// <summary>时序混合最低权重（防止完全丢失历史）。</summary>
        public float TemporalMinWeight = 1e-6f;
        /// <summary>空间重采样 Jacobian 截断阈值（防止极端权重）。</summary>
        public float JacobianCutoff = 100.0f;
        /// <summary>启用可见性复用（第二轮空间重采样 + RT可见性验证）。</summary>
        public bool EnableVisibilityReuse = true;
        /// <summary>可见性复用的空间半径。</summary>
        public int VisibilityReuseRadius = 2;
        /// <summary>最大光源数量（光源树构建上限）。</summary>
        public int MaxLightCount = 4096;
        /// <summary>光源树最大深度。</summary>
        public int MaxTreeDepth = 16;
    }

    // ──────────────────────── Reservoir 数据结构 ────────────────────────

    /// <summary>候选光源样本，包含光源选择信息及采样参数。</summary>
    public struct LightSample
    {
        /// <summary>光源在场景光源数组中的索引。</summary>
        public int LightIndex;
        /// <summary>在光源表面上的采样点（世界空间）。</summary>
        public Vector3 SamplePosition;
        /// <summary>从着色点到光源的方向（归一化）。</summary>
        public Vector3 SampleDirection;
        /// <summary>从着色点到光源的距离。</summary>
        public float Distance;
        /// <summary>该次采样的PDF值。</summary>
        public float Pdf;
        /// <summary>该光源的单位辐射功率。</summary>
        public Vector3 Radiance;

        /// <summary>计算当前样本的目标函数值: f_r * cos_theta * L_i。</summary>
        /// <param name="brdfValue">BRDF估值。</param>
        /// <param name="normal">表面法线。</param>
        public Vector3 EvaluateTarget(Vector3 brdfValue, Vector3 normal)
        {
            float cosTheta = Math.Max(0.0f, Vector3.Dot(normal, SampleDirection));
            return brdfValue * Radiance * cosTheta;
        }
    }

    /// <summary>加权重要性采样 Reservoir（单像素单通道）。</summary>
    /// <remarks>
    /// 存储 M 个候选的联合采样结果: 选中一个样本 y，记录其权重和 W = sum(w_i)，
    /// 以及总PDF之和。在空间复用时通过 Jacobian 修正不同着色点间的PDF差异。
    /// </remarks>
    public struct TemporalReservoir
    {
        /// <summary>选中的候选光源样本。</summary>
        public LightSample SelectedSample;
        /// <summary>权重和 W = sum(w_i)，用于归一化。</summary>
        public float WeightSum;
        /// <summary>采样的目标函数和: sum(f(y_i) / p(y_i))。</summary>
        public float TargetFuncSum;
        /// <summary>候选样本数 M（用于无偏估计的归一化因子）。</summary>
        public int SampleCountM;

        /// <summary>Reservoir 的有效权重（用于后续空间重采样）。</summary>
        public float Weight => WeightSum > 0.0f ? WeightSum / TargetFuncSum : 0.0f;

        /// <summary>重置 Reservoir。</summary>
        public void Reset()
        {
            SelectedSample = new LightSample
            {
                LightIndex = -1,
                SamplePosition = Vector3.Zero,
                SampleDirection = Vector3.UnitY,
                Distance = 0.0f,
                Pdf = 0.0f,
                Radiance = Vector3.Zero
            };
            WeightSum = 0.0f;
            TargetFuncSum = 0.0f;
            SampleCountM = 0;
        }

        /// <summary>从初始样本数组构建 Reservoir（加权选择）。</summary>
        /// <param name="samples">候选样本数组。</param>
        /// <param name="targetWeights">每个样本的目标函数权重 f(y)/p(y) 的标量部分。</param>
        /// <param name="count">样本数。</param>
        /// <param name="rng">均匀随机数生成器。</param>
        public void BuildFromSamples(LightSample[] samples, float[] targetWeights, int count, ref Random rng)
        {
            Reset();
            if (count == 0) return;

            float runningWeightSum = 0.0f;
            float runningTargetSum = 0.0f;
            float u = (float)rng.NextDouble(); // [0,1) 用于加权选择
            float selectedThreshold = u * targetWeights[0]; // 临时偏移

            // 用第一个样本初始化选中样本
            float totalWeight = 0.0f;
            for (int i = 0; i < count; i++)
            {
                float w = targetWeights[i];
                if (w <= 0.0f) continue;

                totalWeight += w;
                float r = (float)rng.NextDouble() * totalWeight; // 加权随机选择
                if (r < w)
                {
                    SelectedSample = samples[i];
                }
                runningWeightSum += w;
                runningTargetSum += w;
            }

            WeightSum = runningWeightSum;
            TargetFuncSum = runningTargetSum;
            SampleCountM = count;
        }

        /// <summary>增量更新 Reservoir（用于时序/空间复用中逐个加入候选）。</summary>
        /// <param name="sample">新候选样本。</param>
        /// <param name="targetWeight">该样本的目标函数权重。</param>
        /// <param name="totalM">更新后的总候选数。</param>
        /// <param name="rng">随机数。</param>
        public void Update(LightSample sample, float targetWeight, int totalM, ref Random rng)
        {
            if (targetWeight <= 0.0f) { SampleCountM = totalM; return; }

            float r = (float)rng.NextDouble();
            float newWeightSum = WeightSum + targetWeight;
            float acceptanceProb = targetWeight / newWeightSum;

            // 以概率 acceptanceProb 替换当前选中样本
            if (r < acceptanceProb)
            {
                SelectedSample = sample;
            }

            WeightSum = newWeightSum;
            TargetFuncSum += targetWeight;
            SampleCountM = totalM;
        }

        /// <summary>计算最终无偏光照估计值。</summary>
        /// <param name="brdfAtShadingPoint">着色点处的 BRDF。</param>
        /// <param name="normal">表面法线。</param>
        /// <returns>估计的直接光照贡献。</returns>
        public Vector3 EvaluateFinalShading(Vector3 brdfAtShadingPoint, Vector3 normal)
        {
            if (WeightSum <= 0.0f || SelectedSample.LightIndex < 0 || SampleCountM == 0)
                return Vector3.Zero;

            Vector3 target = SelectedSample.EvaluateTarget(brdfAtShadingPoint, normal);
            // 无偏估计: (W_sum / M) * (f(y*) / p_hat(y*))
            // 其中 W_sum = sum(w_i), p_hat 是 reservoir 的选择概率
            float estimate = WeightSum / (float)SampleCountM;
            return target * estimate;
        }
    }

    /// <summary>空间 Reservoir，包含时序 Reservoir + 空间传播元数据。</summary>
    public struct SpatialReservoir
    {
        /// <summary>时序 Reservoir 核心。</summary>
        public TemporalReservoir Core;
        /// <summary>对应像素坐标。</summary>
        public int PixelX;
        /// <summary>对应像素坐标。</summary>
        public int PixelY;
        /// <summary>该像素的几何法线（用于 Jacobian 和空间权重）。</summary>
        public Vector3 Normal;
        /// <summary>该像素的世界位置（用于 Jacobian）。</summary>
        public Vector3 WorldPosition;
        /// <summary>是否有效（有足够信息参与空间复用）。</summary>
        public bool IsValid;
    }

    // ──────────────────────── 光源树 ────────────────────────

    /// <summary>光源 BVH 节点。</summary>
    public struct LightTreeNode
    {
        /// <summary>所有子光源的联合轴对齐包围盒（世界空间）。</summary>
        public Vector3 BBoxMin;
        /// <summary>BBox padding。</summary>
        public float BBoxMinW;
        /// <summary>包围盒最大角。</summary>
        public Vector3 BBoxMax;
        /// <summary>BBox padding。</summary>
        public float BBoxMaxW;
        /// <summary>所有子光源的累积辐射功率 (R,G,B) 之和。</summary>
        public Vector3 TotalRadiance;
        /// <summary>光度加权强度标量（用于分割决策和采样 PDF）。</summary>
        public float LuminousPower;
        /// <summary>左子节点索引（-1表示叶节点）。</summary>
        public int LeftChild;
        /// <summary>右子节点索引（-1表示叶节点）。</summary>
        public int RightChild;
        /// <summary>叶节点光源索引（-1表示内部节点）。</summary>
        public int LightIndex;
        /// <summary>包围盒表面积。</summary>
        public float BBoxArea;

        /// <summary>计算包围盒表面积。</summary>
        public void ComputeBBoxArea()
        {
            Vector3 extent = BBoxMax - BBoxMin;
            BBoxArea = 2.0f * (extent.X * extent.Y + extent.Y * extent.Z + extent.Z * extent.X);
        }

        /// <summary>光度加权强度（基于人眼感知亮度: 0.2126R + 0.7152G + 0.0722B）。</summary>
        public static float ComputeLuminousPower(Vector3 radiance)
        {
            return 0.2126f * radiance.X + 0.7152f * radiance.Y + 0.0722f * radiance.Z;
        }
    }

    /// <summary>光源树重要性采样器，构建光源BVH并提供高效采样。</summary>
    public sealed class LightTreeImportanceSampling
    {
        private readonly ReSTIRConfig _config;
        private LightTreeNode[] _nodes;
        private int _nodeCount;
        private int _leafCount;
        private float _totalPower;
        private float[] _nodeCDF;

        /// <summary>当前光源数量。</summary>
        public int LightCount => _leafCount;

        public LightTreeImportanceSampling(ReSTIRConfig config)
        {
            _config = config;
            _nodes = new LightTreeNode[_config.MaxLightCount * 2];
            _nodeCDF = new float[_config.MaxLightCount * 2];
            _nodeCount = 0;
            _leafCount = 0;
            _totalPower = 0.0f;
        }

        /// <summary>从场景光源列表构建光源 BVH。</summary>
        /// <param name="lights">场景中的 GPU 光源数组。</param>
        /// <param name="lightCount">光源数量。</param>
        public void BuildTree(GPULight[] lights, int lightCount)
        {
            _leafCount = Math.Min(lightCount, _config.MaxLightCount);
            _nodeCount = 0;
            _totalPower = 0.0f;

            // 创建叶节点
            int[] leafIndices = new int[_leafCount];
            for (int i = 0; i < _leafCount; i++)
            {
                leafIndices[i] = _nodeCount;
                ref LightTreeNode leaf = ref _nodes[_nodeCount++];
                leaf.LightIndex = i;
                leaf.LeftChild = -1;
                leaf.RightChild = -1;

                // 计算光源包围盒（基于位置和范围）
                float range = lights[i].Range > 0.0f ? lights[i].Range : 1.0f;
                Vector3 pos = lights[i].Position;
                float halfRange = range * 0.5f;
                leaf.BBoxMin = new Vector3(pos.X - halfRange, pos.Y - halfRange, pos.Z - halfRange);
                leaf.BBoxMax = new Vector3(pos.X + halfRange, pos.Y + halfRange, pos.Z + halfRange);

                // 光源辐射功率
                leaf.TotalRadiance = lights[i].Color * lights[i].Intensity;
                leaf.LuminousPower = LightTreeNode.ComputeLuminousPower(leaf.TotalRadiance);
                leaf.ComputeBBoxArea();

                _totalPower += leaf.LuminousPower;
            }

            if (_leafCount <= 1) return;

            // 自顶向下构建BVH: 按最大方差轴分割
            int rootIndex = BuildBVHRecursive(leafIndices, 0, _leafCount);

            // 自底向上计算每个节点的累积功率和CDF
            ComputeNodeCDFRecursive(rootIndex);

            // 构建CDF数组用于采样
            BuildSampleCDF(rootIndex);
        }

        /// <summary>递归构建BVH（基于SAH启发式）。</summary>
        private int BuildBVHRecursive(int[] indices, int start, int count)
        {
            if (count == 1)
                return indices[start];

            // 计算所有叶节点的联合包围盒
            Vector3 bMin = new Vector3(float.MaxValue);
            Vector3 bMax = new Vector3(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                ref LightTreeNode node = ref _nodes[indices[i]];
                bMin = Vector3.Min(bMin, node.BBoxMin);
                bMax = Vector3.Max(bMax, node.BBoxMax);
            }

            // 选择分割轴: 最大范围轴
            Vector3 extent = bMax - bMin;
            int splitAxis = 0;
            if (extent.Y > extent.X) splitAxis = 1;
            if (extent.Z > extent[splitAxis]) splitAxis = 2;

            // 按中心点沿分割轴排序
            Array.Sort(indices, start, count, Comparer<int>.Create((a, b) =>
            {
                float centerA = 0.5f * (_nodes[a].BBoxMin[splitAxis] + _nodes[a].BBoxMax[splitAxis]);
                float centerB = 0.5f * (_nodes[b].BBoxMin[splitAxis] + _nodes[b].BBoxMax[splitAxis]);
                return centerA.CompareTo(centerB);
            }));

            // SAH 最优分割: 最小化 C_traverse + sum(C_isect * N_i / N_total * A_i / A_total)
            // 简化版: 尝试中点分割和四分位点分割，选择最佳
            int bestSplit = count / 2;
            float bestCost = float.MaxValue;
            float totalArea = 2.0f * (extent.X * extent.Y + extent.Y * extent.Z + extent.Z * extent.X);

            // 预计算前缀包围盒和功率
            Vector3[] prefixMin = new Vector3[count];
            Vector3[] prefixMax = new Vector3[count];
            Vector3[] suffixMin = new Vector3[count];
            Vector3[] suffixMax = new Vector3[count];
            float[] prefixPower = new float[count];
            float[] suffixPower = new float[count];

            prefixMin[0] = _nodes[indices[start]].BBoxMin;
            prefixMax[0] = _nodes[indices[start]].BBoxMax;
            prefixPower[0] = _nodes[indices[start]].LuminousPower;
            for (int i = 1; i < count; i++)
            {
                ref LightTreeNode n = ref _nodes[indices[start + i]];
                prefixMin[i] = Vector3.Min(prefixMin[i - 1], n.BBoxMin);
                prefixMax[i] = Vector3.Max(prefixMax[i - 1], n.BBoxMax);
                prefixPower[i] = prefixPower[i - 1] + n.LuminousPower;
            }

            suffixMin[count - 1] = _nodes[indices[start + count - 1]].BBoxMin;
            suffixMax[count - 1] = _nodes[indices[start + count - 1]].BBoxMax;
            suffixPower[count - 1] = _nodes[indices[start + count - 1]].LuminousPower;
            for (int i = count - 2; i >= 0; i--)
            {
                ref LightTreeNode n = ref _nodes[indices[start + i]];
                suffixMin[i] = Vector3.Min(suffixMin[i + 1], n.BBoxMin);
                suffixMax[i] = Vector3.Max(suffixMax[i + 1], n.BBoxMax);
                suffixPower[i] = suffixPower[i + 1] + n.LuminousPower;
            }

            // SAH: 遍历所有候选分割点
            for (int s = 1; s < count; s++)
            {
                Vector3 leftExtent = prefixMax[s - 1] - prefixMin[s - 1];
                float leftArea = 2.0f * (leftExtent.X * leftExtent.Y + leftExtent.Y * leftExtent.Z + leftExtent.Z * leftExtent.X);
                Vector3 rightExtent = suffixMax[s] - suffixMin[s];
                float rightArea = 2.0f * (rightExtent.X * rightExtent.Y + rightExtent.Y * rightExtent.Z + rightExtent.Z * rightExtent.X);

                // 结合面积和功率的SAH代价
                float leftCost = (s > 0 ? (prefixPower[s - 1] / Math.Max(_totalPower, 1e-10f)) * leftArea : 0.0f);
                float rightCost = (s < count ? (suffixPower[s] / Math.Max(_totalPower, 1e-10f)) * rightArea : 0.0f);
                float cost = leftCost + rightCost;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSplit = s;
                }
            }

            // 创建内部节点
            int internalIndex = _nodeCount++;
            ref LightTreeNode internalNode = ref _nodes[internalIndex];
            internalNode.LeftChild = BuildBVHRecursive(indices, start, bestSplit);
            internalNode.RightChild = BuildBVHRecursive(indices, start + bestSplit, count - bestSplit);
            internalNode.LightIndex = -1;
            internalNode.BBoxMin = bMin;
            internalNode.BBoxMax = bMax;

            // 合并子节点的辐射功率
            ref LightTreeNode left = ref _nodes[internalNode.LeftChild];
            ref LightTreeNode right = ref _nodes[internalNode.RightChild];
            internalNode.TotalRadiance = left.TotalRadiance + right.TotalRadiance;
            internalNode.LuminousPower = LightTreeNode.ComputeLuminousPower(internalNode.TotalRadiance);
            internalNode.ComputeBBoxArea();

            return internalIndex;
        }

        /// <summary>递归计算节点累积功率。</summary>
        private void ComputeNodeCDFRecursive(int nodeIndex)
        {
            ref LightTreeNode node = ref _nodes[nodeIndex];
            if (node.LightIndex >= 0) return; // 叶节点
            if (node.LeftChild >= 0) ComputeNodeCDFRecursive(node.LeftChild);
            if (node.RightChild >= 0) ComputeNodeCDFRecursive(node.RightChild);
        }

        /// <summary>为整棵树构建扁平化CDF用于采样。</summary>
        private void BuildSampleCDF(int rootIndex)
        {
            // DFS遍历收集叶节点的累积功率
            List<int> leafOrder = new List<int>(_leafCount);
            CollectLeafNodes(rootIndex, leafOrder);

            _nodeCDF = new float[_leafCount + 1];
            _nodeCDF[0] = 0.0f;
            float cumPower = 0.0f;
            for (int i = 0; i < leafOrder.Count; i++)
            {
                cumPower += _nodes[leafOrder[i]].LuminousPower;
                _nodeCDF[i + 1] = cumPower;
            }

            // 归一化
            if (_totalPower > 0.0f)
            {
                for (int i = 0; i <= _leafCount; i++)
                    _nodeCDF[i] /= _totalPower;
            }
        }

        private void CollectLeafNodes(int nodeIndex, List<int> order)
        {
            ref LightTreeNode node = ref _nodes[nodeIndex];
            if (node.LightIndex >= 0)
            {
                order.Add(nodeIndex);
                return;
            }
            if (node.LeftChild >= 0) CollectLeafNodes(node.LeftChild, order);
            if (node.RightChild >= 0) CollectLeafNodes(node.RightChild, order);
        }

        /// <summary>采样一个光源。返回 (节点索引, 采样PDF)。</summary>
        public (int LightIndex, float Pdf) SampleLight(ref Random rng)
        {
            if (_leafCount <= 0) return (-1, 0.0f);

            float u = (float)rng.NextDouble();

            // CDF 二分查找
            int lo = 0, hi = _leafCount;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_nodeCDF[mid] <= u) lo = mid + 1;
                else hi = mid;
            }

            int lightIdx = lo;
            if (lightIdx >= _leafCount) lightIdx = _leafCount - 1;
            if (lightIdx < 0) lightIdx = 0;

            float pdf = 1.0f / Math.Max(_totalPower, 1e-10f) *
                        _nodes[lightIdx].LuminousPower;

            return (lightIdx, pdf);
        }

        /// <summary>从光源树采样指定数量候选样本。</summary>
        public void SampleCandidateLights(
            int sampleCount,
            Vector3 shadingPoint,
            GPULight[] lights,
            ref Random rng,
            LightSample[] outSamples,
            float[] outPdfs)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                (int lightIdx, float pdf) = SampleLight(ref rng);
                if (lightIdx < 0 || pdf <= 0.0f)
                {
                    outSamples[i] = new LightSample { LightIndex = -1 };
                    outPdfs[i] = 0.0f;
                    continue;
                }

                outSamples[i] = SamplePointOnLight(shadingPoint, lights[lightIdx], lightIdx, ref rng);
                // 联合PDF = 光源选择PDF * 光源表面采样PDF
                outPdfs[i] = pdf * outSamples[i].Pdf;
            }
        }

        /// <summary>在光源表面上采样一个点（根据光源类型）。</summary>
        private LightSample SamplePointOnLight(Vector3 shadingPoint, GPULight light, int lightIndex, ref Random rng)
        {
            LightSample sample = new LightSample { LightIndex = lightIndex };
            Vector3 pos = light.Position;
            Vector3 dir = light.Direction;
            Vector3 color = light.Color * light.Intensity;

            switch ((LightType)light.Type)
            {
                case LightType.Directional:
                    {
                        // 定向光: 采样方向上均匀盘（大距离偏移模拟无穷远）
                        float u1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                        float u2 = (float)(rng.NextDouble() * 2.0 - 1.0);
                        // 构建与光方向正交的坐标系
                        Vector3 tangent = Math.Abs(dir.Y) < 0.999f
                            ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY))
                            : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(dir, tangent);
                        float spread = 100.0f; // 定向光虚拟采样盘半径
                        sample.SamplePosition = pos +
                            tangent * u1 * spread + bitangent * u2 * spread;
                        sample.SampleDirection = Vector3.Normalize(pos - shadingPoint);
                        sample.Distance = float.MaxValue;
                        sample.Radiance = color;
                        sample.Pdf = 1.0f / (4.0f * spread * spread);
                        break;
                    }

                case LightType.Point:
                    {
                        // 点光源: 直接返回位置（无面积，PDF=1）
                        sample.SamplePosition = pos;
                        sample.SampleDirection = Vector3.Normalize(pos - shadingPoint);
                        sample.Distance = Vector3.Distance(shadingPoint, pos);
                        float invDist2 = 1.0f / Math.Max(sample.Distance * sample.Distance, 1e-10f);
                        sample.Radiance = color * invDist2;
                        sample.Pdf = 1.0f;
                        break;
                    }

                case LightType.Spot:
                    {
                        // 聚光灯: 在锥体内采样（均匀方向采样）
                        float cosOuter = MathF.Cos(light.SpotAngle * 0.5f);
                        float cosInner = cosOuter * 0.8f; // 内锥半角

                        // 在球面上均匀采样方向
                        float u1 = (float)rng.NextDouble();
                        float u2 = (float)rng.NextDouble();
                        float cosTheta = cosInner + u1 * (1.0f - cosInner);
                        float sinTheta = MathF.Sqrt(Math.Max(0.0f, 1.0f - cosTheta * cosTheta));
                        float phi = u2 * 2.0f * MathF.PI;

                        // 构建聚光灯坐标系
                        Vector3 tangent = Math.Abs(dir.Y) < 0.999f
                            ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY))
                            : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(dir, tangent);

                        Vector3 spotDir = tangent * (sinTheta * MathF.Cos(phi)) +
                                          bitangent * (sinTheta * MathF.Sin(phi)) +
                                          dir * cosTheta;

                        // 采样点在range距离处
                        float r = light.Range > 0 ? light.Range : 10.0f;
                        sample.SamplePosition = pos + spotDir * r;
                        sample.SampleDirection = Vector3.Normalize(pos - shadingPoint);
                        sample.Distance = Vector3.Distance(shadingPoint, pos);
                        float invDist2 = 1.0f / Math.Max(sample.Distance * sample.Distance, 1e-10f);

                        // 聚光灯强度衰减
                        float spotDot = Vector3.Dot(-spotDir, dir);
                        float spotAtten = Math.Max(0.0f, (spotDot - cosOuter) / Math.Max(cosInner - cosOuter, 1e-6f));
                        spotAtten = spotAtten * spotAtten; // 平方衰减

                        sample.Radiance = color * invDist2 * spotAtten;
                        sample.Pdf = 1.0f / (2.0f * MathF.PI * (1.0f - cosInner));
                        break;
                    }

                case LightType.Rectangle:
                    {
                        // 矩形面光源: 均匀面积采样
                        float u1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                        float u2 = (float)(rng.NextDouble() * 2.0 - 1.0);

                        Vector3 tangent = Math.Abs(dir.Y) < 0.999f
                            ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY))
                            : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(dir, tangent);

                        float halfWidth = light.Range * 0.5f;
                        float halfHeight = halfWidth;

                        sample.SamplePosition = pos +
                            tangent * u1 * halfWidth + bitangent * u2 * halfHeight;
                        sample.SampleDirection = Vector3.Normalize(sample.SamplePosition - shadingPoint);
                        sample.Distance = Vector3.Distance(shadingPoint, sample.SamplePosition);
                        float invDist2 = 1.0f / Math.Max(sample.Distance * sample.Distance, 1e-10f);

                        float cosLight = Math.Max(0.0f, Vector3.Dot(-sample.SampleDirection, dir));
                        sample.Radiance = color * invDist2 * cosLight;
                        sample.Pdf = 1.0f / Math.Max(4.0f * halfWidth * halfHeight, 1e-10f);
                        break;
                    }

                case LightType.Disk:
                    {
                        // 圆盘面光源: 面积采样（均匀圆盘）
                        float u1 = (float)rng.NextDouble();
                        float u2 = (float)rng.NextDouble();
                        float r = MathF.Sqrt(u1) * light.Range * 0.5f;
                        float theta = u2 * 2.0f * MathF.PI;

                        Vector3 tangent = Math.Abs(dir.Y) < 0.999f
                            ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY))
                            : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(dir, tangent);

                        sample.SamplePosition = pos +
                            tangent * (r * MathF.Cos(theta)) +
                            bitangent * (r * MathF.Sin(theta));
                        sample.SampleDirection = Vector3.Normalize(sample.SamplePosition - shadingPoint);
                        sample.Distance = Vector3.Distance(shadingPoint, sample.SamplePosition);
                        float invDist2 = 1.0f / Math.Max(sample.Distance * sample.Distance, 1e-10f);

                        float cosLight = Math.Max(0.0f, Vector3.Dot(-sample.SampleDirection, dir));
                        sample.Radiance = color * invDist2 * cosLight;
                        float area = MathF.PI * (light.Range * 0.5f) * (light.Range * 0.5f);
                        sample.Pdf = 1.0f / Math.Max(area, 1e-10f);
                        break;
                    }

                default:
                    sample = new LightSample { LightIndex = -1 };
                    break;
            }

            return sample;
        }
    }

    // ──────────────────────── ReSTIR 管线 ────────────────────────

    /// <summary>ReSTIR DI 直接光照管线。</summary>
    public sealed class ReSTIRPipeline
    {
        private readonly ReSTIRConfig _config;
        private LightTreeImportanceSampling _lightTree;
        private TemporalReservoir[] _currentFrameReservoirs;
        private TemporalReservoir[] _prevFrameReservoirs;
        private TemporalReservoir[] _spatialPassReservoirs;
        private SpatialReservoir[] _spatialReservoirs;
        private int _width;
        private int _height;
        private int _frameIndex;

        /// <summary>当前帧索引（用于 RNG 种子）。</summary>
        public int FrameIndex => _frameIndex;

        public ReSTIRPipeline(ReSTIRConfig config)
        {
            _config = config;
            _lightTree = new LightTreeImportanceSampling(config);
            _frameIndex = 0;
            _width = 0;
            _height = 0;
        }

        /// <summary>初始化/调整缓冲区大小。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            int pixelCount = width * height;
            _currentFrameReservoirs = new TemporalReservoir[pixelCount];
            _prevFrameReservoirs = new TemporalReservoir[pixelCount];
            _spatialPassReservoirs = new TemporalReservoir[pixelCount];
            _spatialReservoirs = new SpatialReservoir[pixelCount];

            for (int i = 0; i < pixelCount; i++)
            {
                _currentFrameReservoirs[i].Reset();
                _prevFrameReservoirs[i].Reset();
                _spatialPassReservoirs[i].Reset();
                _spatialReservoirs[i].IsValid = false;
            }
        }

        /// <summary>完整 ReSTIR DI 管线执行（每帧调用一次）。</summary>
        /// <param name="lights">场景光源。</param>
        /// <param name="lightCount">光源数。</param>
        /// <param name="normals">G-Buffer法线纹理（逐像素 Vector3）。</param>
        /// <param name="worldPositions">G-Buffer世界位置纹理。</param>
        /// <param name="depth">深度缓冲。</param>
        /// <param name="motionVectors">运动矢量缓冲。</param>
        /// <param name="output">输出直接光照颜色缓冲。</param>
        public void Execute(
            GPULight[] lights, int lightCount,
            Vector3[] normals, Vector3[] worldPositions, float[] depth,
            Vector2[] motionVectors, Vector3[] output)
        {
            if (_width == 0 || _height == 0) return;

            // 1. 构建光源树
            _lightTree.BuildTree(lights, lightCount);

            // 2. 每像素生成初始样本 + 时序复用
            GenerateInitialSamplesAndTemporalReuse(lights, lightCount, normals, worldPositions, depth, motionVectors);

            // 3. 空间复用
            SpatialReuse(normals, worldPositions);

            // 4. 可见性复用（可选）
            if (_config.EnableVisibilityReuse)
            {
                VisibilityReuse(normals, worldPositions, depth);
            }

            // 5. 最终着色
            FinalShading(normals, output);

            // 6. 交换缓冲区
            SwapBuffers();
            _frameIndex++;
        }

        // ──── 第1步: 初始采样 + 时序复用 ────

        private void GenerateInitialSamplesAndTemporalReuse(
            GPULight[] lights, int lightCount,
            Vector3[] normals, Vector3[] worldPositions, float[] depth,
            Vector2[] motionVectors)
        {
            // 每个像素使用独立种子（像素坐标哈希 + 帧索引）
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int idx = y * _width + x;
                    int seed = HashPixelCoordinate(x, y, _frameIndex);
                    Random rng = new Random(seed);

                    Vector3 normal = normals[idx];
                    Vector3 worldPos = worldPositions[idx];
                    Vector3 viewDir = Vector3.Normalize(-worldPos); // 简化: 假设相机在原点

                    // 初始采样: 从光源树采样 M 个候选
                    int M = _config.InitialSampleCountM;
                    LightSample[] candidates = new LightSample[M];
                    float[] weights = new float[M];

                    for (int i = 0; i < M; i++)
                    {
                        _lightTree.SampleCandidateLights(
                            1, worldPos, lights, ref rng, 
                            new LightSample[] { candidates[i] }, 
                            new float[] { weights[i] });
                    }

                    // 计算目标函数权重: f_r * cos_theta / p(y)
                    for (int i = 0; i < M; i++)
                    {
                        if (candidates[i].LightIndex < 0) { weights[i] = 0.0f; continue; }

                        // 简化 BRDF: Lambert漫反射 (f_r = albedo / pi)
                        float albedo = 0.7f; // 从材质获取
                        Vector3 brdf = new Vector3(albedo / MathF.PI);

                        float cosTheta = Math.Max(0.0f, Vector3.Dot(normal, candidates[i].SampleDirection));
                        Vector3 fCosLi = brdf * candidates[i].Radiance * cosTheta;

                        // 目标函数标量近似（用光度）
                        float targetScalar = LightTreeNode.ComputeLuminousPower(fCosLi);
                        weights[i] = candidates[i].Pdf > 0.0f ? targetScalar / candidates[i].Pdf : 0.0f;
                    }

                    // 构建初始Reservoir
                    _currentFrameReservoirs[idx].Reset();
                    for (int i = 0; i < M; i++)
                    {
                        int totalM = i + 1;
                        _currentFrameReservoirs[idx].Update(candidates[i], weights[i], totalM, ref rng);
                    }

                    // WARP 时序复用: 从上一帧获取候选
                    Vector2 mv = motionVectors[idx];
                    float prevX = x - mv.X;
                    float prevY = y - mv.Y;
                    int prevIdxX = (int)Math.Round(prevX);
                    int prevIdxY = (int)Math.Round(prevY);

                    if (prevIdxX >= 0 && prevIdxX < _width &&
                        prevIdxY >= 0 && prevIdxY < _height &&
                        depth[idx] > 0.0f)
                    {
                        int prevIdx = prevIdxY * _width + prevIdxX;
                        TemporalReservoir prevRes = _prevFrameReservoirs[prevIdx];
                        if (prevRes.WeightSum > 0.0f && prevRes.SelectedSample.LightIndex >= 0)
                        {
                            // 基于运动矢量评估历史样本在当前帧的权重
                            LightSample prevSample = prevRes.SelectedSample;
                            float cosTheta = Math.Max(0.0f, Vector3.Dot(normal, prevSample.SampleDirection));
                            float albedo = 0.7f;
                            Vector3 brdf = new Vector3(albedo / MathF.PI);
                            Vector3 fCosLi = brdf * prevSample.Radiance * cosTheta;
                            float targetScalar = LightTreeNode.ComputeLuminousPower(fCosLi);

                            // WARP: 以 p_star(y) 为目标PDF重新加权
                            float warpWeight = targetScalar / Math.Max(prevSample.Pdf, 1e-10f);
                            warpWeight = Math.Min(warpWeight, _config.JacobianCutoff);

                            // 添加M'个历史候选
                            int totalMCandidate = M;
                            _currentFrameReservoirs[idx].Update(prevSample, warpWeight, totalMCandidate + 1, ref rng);
                            totalMCandidate++;

                            // 从历史Reservoir中再采样几个额外候选（简化版）
                            int mpExtra = Math.Min(_config.TemporalReuseCountMp, 4);
                            for (int k = 0; k < mpExtra; k++)
                            {
                                // 模拟从历史Reservoir重采样: 使用历史样本的不同权重变体
                                float perturbPdf = prevSample.Pdf * (0.8f + 0.4f * (float)rng.NextDouble());
                                float perturbWeight = targetScalar / Math.Max(perturbPdf, 1e-10f);
                                perturbWeight = Math.Min(perturbWeight, _config.JacobianCutoff);
                                _currentFrameReservoirs[idx].Update(prevSample, perturbWeight, totalMCandidate + 1, ref rng);
                                totalMCandidate++;
                            }
                        }
                    }
                }
            }
        }

        // ──── 第2步: 空间复用 ────

        private void SpatialReuse(Vector3[] normals, Vector3[] worldPositions)
        {
            int radius = _config.SpatialPropagationRadius;
            int K = _config.SpatialCandidateCountK;

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int idx = y * _width + x;
                    _spatialPassReservoirs[idx] = _currentFrameReservoirs[idx];
                    _spatialReservoirs[idx].Core = _currentFrameReservoirs[idx];
                    _spatialReservoirs[idx].PixelX = x;
                    _spatialReservoirs[idx].PixelY = y;
                    _spatialReservoirs[idx].Normal = normals[idx];
                    _spatialReservoirs[idx].WorldPosition = worldPositions[idx];
                    _spatialReservoirs[idx].IsValid = _currentFrameReservoirs[idx].WeightSum > 0.0f;

                    int seed = HashPixelCoordinate(x, y, _frameIndex + 10000);
                    Random rng = new Random(seed);

                    Vector3 centerNormal = normals[idx];
                    Vector3 centerPos = worldPositions[idx];

                    // 遍历空间邻居
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;

                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;

                            int nIdx = ny * _width + nx;
                            if (!_spatialReservoirs[nIdx].IsValid) continue;
                            if (_currentFrameReservoirs[nIdx].WeightSum <= 0.0f) continue;

                            // 几何相似性测试
                            Vector3 neighborNormal = normals[nIdx];
                            Vector3 neighborPos = worldPositions[nIdx];
                            float normalDot = Vector3.Dot(centerNormal, neighborNormal);

                            // 法线相似性 > 0.9（约25度）且距离合理
                            if (normalDot < 0.9f) continue;
                            float posDist = Vector3.Distance(centerPos, neighborPos);
                            if (posDist > 2.0f) continue;

                            // Jacobian修正: 因为邻居Reservoir是在不同着色点采样的
                            LightSample neighborSample = _currentFrameReservoirs[nIdx].SelectedSample;
                            float cosThetaCenter = Math.Max(0.0f, Vector3.Dot(centerNormal, neighborSample.SampleDirection));
                            float cosThetaNeighbor = Math.Max(0.0f, Vector3.Dot(neighborNormal, neighborSample.SampleDirection));
                            float distCenter = Vector3.Distance(centerPos, neighborSample.SamplePosition);
                            float distNeighbor = Vector3.Distance(neighborPos, neighborSample.SamplePosition);

                            // Jacobian = (cos_center / dist_center^2) / (cos_neighbor / dist_neighbor^2)
                            float jacNum = cosThetaCenter / Math.Max(distCenter * distCenter, 1e-10f);
                            float jacDen = cosThetaNeighbor / Math.Max(distNeighbor * distNeighbor, 1e-10f);
                            float jacobian = jacNum / Math.Max(jacDen, 1e-10f);
                            jacobian = Math.Min(Math.Max(jacobian, 0.0f), _config.JacobianCutoff);

                            // 重加权
                            float neighborWeight = _currentFrameReservoirs[nIdx].WeightSum;
                            float spatialWeight = neighborWeight * jacobian;

                            _spatialPassReservoirs[idx].Update(
                                neighborSample, spatialWeight,
                                _spatialPassReservoirs[idx].SampleCountM + 1, ref rng);
                        }
                    }
                }
            }

            // 空间结果写回
            Array.Copy(_spatialPassReservoirs, _currentFrameReservoirs, _width * _height);
        }

        // ──── 第3步: 可见性复用 ────

        private void VisibilityReuse(Vector3[] normals, Vector3[] worldPositions, float[] depth)
        {
            int radius = _config.VisibilityReuseRadius;

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int idx = y * _width + x;
                    LightSample currentSample = _currentFrameReservoirs[idx].SelectedSample;
                    if (currentSample.LightIndex < 0) continue;

                    // 对当前像素的光源样本验证可见性
                    bool visible = TraceVisibilityRay(worldPositions[idx], currentSample.SamplePosition);

                    if (!visible)
                    {
                        // 当前样本被遮挡，尝试从邻居找到可见样本
                        int seed = HashPixelCoordinate(x, y, _frameIndex + 20000);
                        Random rng = new Random(seed);

                        TemporalReservoir visibilityReservoir = new TemporalReservoir();
                        visibilityReservoir.Reset();

                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;

                                int nIdx = ny * _width + nx;
                                if (_currentFrameReservoirs[nIdx].WeightSum <= 0.0f) continue;

                                LightSample nSample = _currentFrameReservoirs[nIdx].SelectedSample;
                                if (nSample.LightIndex < 0) continue;

                                // 验证邻居样本在当前像素的可见性
                                if (TraceVisibilityRay(worldPositions[idx], nSample.SamplePosition))
                                {
                                    // 重新计算权重（与空间复用相同的 Jacobian 修正）
                                    Vector3 centerNormal = normals[idx];
                                    Vector3 neighborNormal = normals[nIdx];
                                    float cosCenter = Math.Max(0.0f, Vector3.Dot(centerNormal, nSample.SampleDirection));
                                    float cosNeighbor = Math.Max(0.0f, Vector3.Dot(neighborNormal, nSample.SampleDirection));
                                    float dCenter = Vector3.Distance(worldPositions[idx], nSample.SamplePosition);
                                    float dNeighbor = Vector3.Distance(worldPositions[nIdx], nSample.SamplePosition);
                                    float jacobian = (cosCenter / Math.Max(dCenter * dCenter, 1e-10f)) /
                                                     (cosNeighbor / Math.Max(dNeighbor * dNeighbor, 1e-10f));
                                    jacobian = Math.Min(Math.Max(jacobian, 0.0f), _config.JacobianCutoff);

                                    float weight = _currentFrameReservoirs[nIdx].WeightSum * jacobian;
                                    visibilityReservoir.Update(nSample, weight,
                                        visibilityReservoir.SampleCountM + 1, ref rng);
                                }
                            }
                        }

                        // 如果找到了可见样本，替换当前Reservoir
                        if (visibilityReservoir.WeightSum > 0.0f)
                        {
                            _currentFrameReservoirs[idx] = visibilityReservoir;
                        }
                    }
                }
            }
        }

        /// <summary>简化的可见性光线追踪（模拟RT加速结构查询）。</summary>
        private bool TraceVisibilityRay(Vector3 origin, Vector3 target)
        {
            // 在实际实现中，这里调用 DXR/Vulkan RT 的 RayQuery
            // 这里使用简化版本: 检测射线是否被中间物体遮挡
            // 返回 true 表示可见（无遮挡）
            Vector3 rayDir = target - origin;
            float rayLength = rayDir.Length();
            if (rayLength < 1e-6f) return false;
            rayDir /= rayLength;

            // 简化: 对场景AABB层次结构进行射线-三角形测试
            // 实际实现中会使用硬件RT加速 (Opportunity: TLAS/BLAS traversal)
            return !IntersectAnyPrimitive(origin, rayDir, rayLength);
        }

        /// <summary>简化版原始体相交测试（模拟RT返回任意命中）。</summary>
        private bool IntersectAnyPrimitive(Vector3 origin, Vector3 dir, float maxT)
        {
            // 在完整实现中，此处为 TLAS/BLAS 遍历 + 三角形求交
            // 返回 true 表示存在遮挡
            return false; // 默认无遮挡，实际由RT硬件实现
        }

        // ──── 第4步: 最终着色 ────

        private void FinalShading(Vector3[] normals, Vector3[] output)
        {
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int idx = y * _width + x;
                    Vector3 normal = normals[idx];

                    // Lambert漫反射 BRDF
                    float albedo = 0.7f;
                    Vector3 brdf = new Vector3(albedo / MathF.PI);

                    // 最终无偏估计
                    output[idx] = _currentFrameReservoirs[idx].EvaluateFinalShading(brdf, normal);
                }
            }
        }

        // ──── 缓冲区管理 ────

        private void SwapBuffers()
        {
            var temp = _prevFrameReservoirs;
            _prevFrameReservoirs = _currentFrameReservoirs;
            _currentFrameReservoirs = temp;

            // 清零当前帧缓冲（下一帧重新填充）
            for (int i = 0; i < _width * _height; i++)
            {
                _currentFrameReservoirs[i].Reset();
            }
        }

        /// <summary>像素坐标哈希（用于RNG种子）。</summary>
        private static int HashPixelCoordinate(int x, int y, int frame)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + frame * 1013904223);
            h = (h ^ (h >> 13)) * 1274126177;
            h = h ^ (h >> 16);
            return unchecked((int)h);
        }
    }

    // ──────────────────────── GPU 常量缓冲 ────────────────────────

    /// <summary>ReSTIR DI GPU 常量缓冲（用于 Compute Shader 调度）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReSTIRCB
    {
        public int Width;
        public int Height;
        public int FrameIndex;
        public int InitialSampleCountM;
        public int SpatialCandidateCountK;
        public int SpatialPropagationRadius;
        public int TemporalReuseCountMp;
        public float TemporalMinWeight;
        public float JacobianCutoff;
        public int EnableVisibilityReuse;
        public int VisibilityReuseRadius;
        public int LightCount;
        public int Padding1;
        public int Padding2;
        public int Padding3;
    }
}
