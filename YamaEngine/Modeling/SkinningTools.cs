// =====================================================================================
//  YamaEngine.Modeling.SkinningTools
//  骨骼绑定工具系统 (Skinning & Rigging Tools)
// -------------------------------------------------------------------------------------
//  自动骨骼绑定 + 权重绘制:
//    - Bone / SkeletonRig: 骨骼层级 (根骨骼→子骨骼递归)
//    - AutoRigger: 从 AABB 生成人形骨骼 / 热扩散权重 / 体素化权重
//    - WeightPainting: 权重绘制 (Add / Subtract / Smooth / Normalize)
//    - SkinningValidator: 权重缺失检测 / 影响过多检测 / 骨骼层级验证
//    - SkinWeights: 顶点蒙皮权重存储
//
//  与已有 EditableMesh.BoneWeights / MeshBoneWeight 共存, 使用独立类型。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  Bone — 骨骼
    // =================================================================================

    /// <summary>
    /// 骨骼 —— 骨骼层级中的一个节点。
    /// </summary>
    public class Bone
    {
        /// <summary>骨骼名称。</summary>
        public string Name;

        /// <summary>父骨骼索引 (-1 = 根骨骼)。</summary>
        public int Parent = -1;

        /// <summary>骨骼索引 (在 SkeletonRig 中的位置)。</summary>
        public int Index;

        /// <summary>绑定姿态的局部变换矩阵 (相对父骨骼)。</summary>
        public Matrix4x4 RestMatrix = Matrix4x4.Identity;

        /// <summary>绑定姿态的逆世界变换矩阵 (用于蒙皮)。</summary>
        public Matrix4x4 BindMatrix = Matrix4x4.Identity;

        /// <summary>骨骼在绑定姿态下的世界位置 (头部)。</summary>
        public Vector3 HeadPosition;

        /// <summary>骨骼在绑定姿态下的世界位置 (尾部)。</summary>
        public Vector3 TailPosition;

        /// <summary>子骨骼索引列表。</summary>
        public List<int> Children = new();

        public Bone(string name, int index, int parent = -1)
        {
            Name = name;
            Index = index;
            Parent = parent;
        }

        /// <summary>骨骼方向 (Head -> Tail 的单位向量)。</summary>
        public Vector3 Direction
        {
            get
            {
                var dir = TailPosition - HeadPosition;
                return dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitY;
            }
        }

        /// <summary>骨骼长度。</summary>
        public float Length => Vector3.Distance(HeadPosition, TailPosition);

        public override string ToString() => $"Bone[{Index}] '{Name}' (parent={Parent})";
    }

    // =================================================================================
    //  SkeletonRig — 骨骼层级
    // =================================================================================

    /// <summary>
    /// 骨骼层级 —— 管理一组骨骼的层级关系。
    /// 支持层级遍历 (根骨骼→子骨骼递归) 和世界变换计算。
    /// </summary>
    public class SkeletonRig
    {
        public List<Bone> Bones = new();
        public List<int> RootBones = new();

        /// <summary>添加骨骼到层级中。</summary>
        public Bone AddBone(string name, int parent = -1, Vector3 head = default, Vector3 tail = default)
        {
            int index = Bones.Count;
            var bone = new Bone(name, index, parent)
            {
                HeadPosition = head,
                TailPosition = tail
            };

            if (parent >= 0 && parent < Bones.Count)
                Bones[parent].Children.Add(index);
            else
                RootBones.Add(index);

            Bones.Add(bone);
            return bone;
        }

        /// <summary>计算所有骨骼的世界变换矩阵和绑定矩阵。</summary>
        public void ComputeBindMatrices()
        {
            foreach (var rootIdx in RootBones)
                ComputeWorldTransform(rootIdx, Matrix4x4.Identity);
        }

        private void ComputeWorldTransform(int boneIdx, Matrix4x4 parentWorld)
        {
            var bone = Bones[boneIdx];
            var world = bone.RestMatrix * parentWorld;

            // 绑定矩阵 = 世界变换的逆
            if (Matrix4x4.Invert(world, out var invWorld))
                bone.BindMatrix = invWorld;
            else
                bone.BindMatrix = Matrix4x4.Identity;

            foreach (var childIdx in bone.Children)
                ComputeWorldTransform(childIdx, world);
        }

        /// <summary>深度优先遍历所有骨骼。</summary>
        public IEnumerable<Bone> TraverseDFS()
        {
            foreach (var rootIdx in RootBones)
                foreach (var bone in TraverseDFS(rootIdx))
                    yield return bone;
        }

        private IEnumerable<Bone> TraverseDFS(int boneIdx)
        {
            yield return Bones[boneIdx];
            foreach (var childIdx in Bones[boneIdx].Children)
                foreach (var bone in TraverseDFS(childIdx))
                    yield return bone;
        }

        /// <summary>获取骨骼的世界变换矩阵 (递归)。</summary>
        public Matrix4x4 GetWorldMatrix(int boneIdx)
        {
            if (boneIdx < 0 || boneIdx >= Bones.Count) return Matrix4x4.Identity;
            var bone = Bones[boneIdx];
            var local = bone.RestMatrix;
            if (bone.Parent >= 0)
                return local * GetWorldMatrix(bone.Parent);
            return local;
        }

        /// <summary>获取骨骼的世界头部位置。</summary>
        public Vector3 GetWorldHead(int boneIdx)
        {
            return Bones[boneIdx].HeadPosition;
        }

        /// <summary>获取骨骼的世界尾部位置。</summary>
        public Vector3 GetWorldTail(int boneIdx)
        {
            return Bones[boneIdx].TailPosition;
        }
    }

    // =================================================================================
    //  SkinWeights — 顶点蒙皮权重存储
    // =================================================================================

    /// <summary>
    /// 顶点蒙皮权重存储 —— 每个顶点存储一个 {骨骼索引: 权重} 字典。
    /// </summary>
    public class SkinWeights
    {
        public List<Dictionary<int, float>> VertexWeights = new();

        public void Init(int vertexCount)
        {
            VertexWeights = new List<Dictionary<int, float>>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
                VertexWeights.Add(new Dictionary<int, float>());
        }

        public float GetWeight(int vertex, int bone)
        {
            return VertexWeights[vertex].TryGetValue(bone, out float w) ? w : 0f;
        }

        public void SetWeight(int vertex, int bone, float weight)
        {
            if (weight <= 0f)
                VertexWeights[vertex].Remove(bone);
            else
                VertexWeights[vertex][bone] = weight;
        }

        public void AddWeight(int vertex, int bone, float delta)
        {
            var current = GetWeight(vertex, bone);
            SetWeight(vertex, bone, Math.Max(0f, current + delta));
        }

        /// <summary>归一化单个顶点的权重 (总和 = 1)。</summary>
        public void NormalizeVertex(int vertex)
        {
            var weights = VertexWeights[vertex];
            if (weights.Count == 0) return;
            float sum = weights.Values.Sum();
            if (sum < 1e-8f) return;

            var keys = weights.Keys.ToList();
            foreach (var key in keys)
                weights[key] /= sum;
        }

        /// <summary>归一化所有顶点。</summary>
        public void NormalizeAll()
        {
            for (int i = 0; i < VertexWeights.Count; i++)
                NormalizeVertex(i);
        }

        /// <summary>获取影响顶点的骨骼列表。</summary>
        public List<int> GetInfluencingBones(int vertex)
        {
            return VertexWeights[vertex].Keys.ToList();
        }

        /// <summary>获取顶点的影响骨骼数量。</summary>
        public int GetInfluenceCount(int vertex)
        {
            return VertexWeights[vertex].Count;
        }

        /// <summary>获取顶点的总权重。</summary>
        public float GetTotalWeight(int vertex)
        {
            return VertexWeights[vertex].Values.Sum();
        }

        /// <summary>平滑单个顶点的权重 (邻居平均)。</summary>
        public void SmoothVertex(int vertex, List<HashSet<int>> adjacency, List<Vector3> positions)
        {
            if (vertex >= adjacency.Count || adjacency[vertex].Count == 0) return;

            var bones = VertexWeights[vertex].Keys.ToList();
            foreach (var bone in bones)
            {
                float sum = GetWeight(vertex, bone);
                int count = 1;
                foreach (var nb in adjacency[vertex])
                {
                    sum += GetWeight(nb, bone);
                    count++;
                }
                SetWeight(vertex, bone, sum / count);
            }
            NormalizeVertex(vertex);
        }
    }

    // =================================================================================
    //  AutoRigger — 自动绑定
    // =================================================================================

    /// <summary>
    /// 自动绑定工具 —— 从网格自动生成骨骼和权重。
    /// </summary>
    public static class AutoRigger
    {
        // ──────────────────────────────────────────────────────────────
        //  GenerateSkeleton — 从 AABB 生成人形骨骼
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 从网格 AABB 生成基础人形骨骼 (头/脊柱/左右臂/左右腿)。
        /// 假设网格大致人形, Y 轴向上。
        /// </summary>
        public static SkeletonRig GenerateSkeleton(List<Vector3> positions, List<(int A, int B, int C)> triangles)
        {
            if (positions.Count == 0) return new SkeletonRig();

            // 计算 AABB
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var center = (min + max) * 0.5f;
            var size = max - min;
            float height = size.Y;

            var rig = new SkeletonRig();

            // 骨骼位置 (假设人形: Y 轴向上, Z 轴向前, X 轴向右)
            float pelvisY = min.Y + height * 0.45f;
            float spineY = min.Y + height * 0.60f;
            float chestY = min.Y + height * 0.75f;
            float neckY = min.Y + height * 0.88f;
            float headY = min.Y + height * 0.95f;

            float shoulderX = size.X * 0.22f;
            float armX = size.X * 0.45f;
            float handX = size.X * 0.50f;
            float armY = min.Y + height * 0.72f;
            float forearmY = min.Y + height * 0.50f;
            float handY = min.Y + height * 0.30f;

            float hipX = size.X * 0.12f;
            float kneeY = min.Y + height * 0.25f;
            float footY = min.Y + height * 0.02f;

            // 中轴骨骼
            var pelvis = rig.AddBone("Pelvis", -1,
                new Vector3(center.X, pelvisY, center.Z),
                new Vector3(center.X, spineY, center.Z));

            var spine = rig.AddBone("Spine", pelvis.Index,
                new Vector3(center.X, spineY, center.Z),
                new Vector3(center.X, chestY, center.Z));

            var chest = rig.AddBone("Chest", spine.Index,
                new Vector3(center.X, chestY, center.Z),
                new Vector3(center.X, neckY, center.Z));

            var neck = rig.AddBone("Neck", chest.Index,
                new Vector3(center.X, neckY, center.Z),
                new Vector3(center.X, headY, center.Z));

            var head = rig.AddBone("Head", neck.Index,
                new Vector3(center.X, headY, center.Z),
                new Vector3(center.X, max.Y, center.Z));

            // 左臂 (X > 0)
            var lShoulder = rig.AddBone("L_Shoulder", chest.Index,
                new Vector3(center.X, armY, center.Z),
                new Vector3(center.X + shoulderX, armY, center.Z));

            var lUpperArm = rig.AddBone("L_UpperArm", lShoulder.Index,
                new Vector3(center.X + shoulderX, armY, center.Z),
                new Vector3(center.X + armX, forearmY, center.Z));

            var lForearm = rig.AddBone("L_Forearm", lUpperArm.Index,
                new Vector3(center.X + armX, forearmY, center.Z),
                new Vector3(center.X + handX, handY, center.Z));

            var lHand = rig.AddBone("L_Hand", lForearm.Index,
                new Vector3(center.X + handX, handY, center.Z),
                new Vector3(center.X + handX, min.Y + height * 0.20f, center.Z));

            // 右臂 (X < 0)
            var rShoulder = rig.AddBone("R_Shoulder", chest.Index,
                new Vector3(center.X, armY, center.Z),
                new Vector3(center.X - shoulderX, armY, center.Z));

            var rUpperArm = rig.AddBone("R_UpperArm", rShoulder.Index,
                new Vector3(center.X - shoulderX, armY, center.Z),
                new Vector3(center.X - armX, forearmY, center.Z));

            var rForearm = rig.AddBone("R_Forearm", rUpperArm.Index,
                new Vector3(center.X - armX, forearmY, center.Z),
                new Vector3(center.X - handX, handY, center.Z));

            var rHand = rig.AddBone("R_Hand", rForearm.Index,
                new Vector3(center.X - handX, handY, center.Z),
                new Vector3(center.X - handX, min.Y + height * 0.20f, center.Z));

            // 左腿
            var lThigh = rig.AddBone("L_Thigh", pelvis.Index,
                new Vector3(center.X + hipX, pelvisY, center.Z),
                new Vector3(center.X + hipX, kneeY, center.Z));

            var lShin = rig.AddBone("L_Shin", lThigh.Index,
                new Vector3(center.X + hipX, kneeY, center.Z),
                new Vector3(center.X + hipX, footY, center.Z));

            var lFoot = rig.AddBone("L_Foot", lShin.Index,
                new Vector3(center.X + hipX, footY, center.Z),
                new Vector3(center.X + hipX, min.Y, center.Z + size.Z * 0.1f));

            // 右腿
            var rThigh = rig.AddBone("R_Thigh", pelvis.Index,
                new Vector3(center.X - hipX, pelvisY, center.Z),
                new Vector3(center.X - hipX, kneeY, center.Z));

            var rShin = rig.AddBone("R_Shin", rThigh.Index,
                new Vector3(center.X - hipX, kneeY, center.Z),
                new Vector3(center.X - hipX, footY, center.Z));

            var rFoot = rig.AddBone("R_Foot", rShin.Index,
                new Vector3(center.X - hipX, footY, center.Z),
                new Vector3(center.X - hipX, min.Y, center.Z + size.Z * 0.1f));

            // 计算绑定矩阵
            rig.ComputeBindMatrices();

            return rig;
        }

        // ──────────────────────────────────────────────────────────────
        //  HeatmapWeights — 热扩散权重
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 热扩散权重计算 (Heatmap Skinning)。
        /// 1. 构建网格顶点的拉普拉斯矩阵 L (余切权重)
        /// 2. 添加正则化: L' = L + h*I
        /// 3. 对每根骨骼求解 L'w = b (热扩散方程, Gauss-Seidel 迭代)
        /// 4. 归一化权重 (每顶点权重和 = 1)
        /// </summary>
        public static SkinWeights HeatmapWeights(
            List<Vector3> positions, List<(int A, int B, int C)> triangles,
            SkeletonRig rig, float heatFactor = 1e-3f, int maxIterations = 200)
        {
            int nVerts = positions.Count;
            int nBones = rig.Bones.Count;
            var weights = new SkinWeights();
            weights.Init(nVerts);

            if (nVerts == 0 || nBones == 0) return weights;

            // 1. 构建余切拉普拉斯矩阵 (稀疏表示)
            var adjacency = new List<List<(int neighbor, float weight)>>(nVerts);
            var diag = new float[nVerts];
            for (int i = 0; i < nVerts; i++) adjacency.Add(new List<(int, float)>());

            var edgeWeights = new Dictionary<(int, int), float>();

            foreach (var (a, b, c) in triangles)
            {
                // 三条边: (a,b)对角c, (b,c)对角a, (c,a)对角b
                AddCotangentWeight(edgeWeights, a, b, c, positions);
                AddCotangentWeight(edgeWeights, b, c, a, positions);
                AddCotangentWeight(edgeWeights, c, a, b, positions);
            }

            // 构建邻接表和对角线
            foreach (var kvp in edgeWeights)
            {
                var (i, j) = kvp.Key;
                float w = kvp.Value;

                // 钳制负权重 (数值稳定性)
                w = Math.Max(w, 0f);

                adjacency[i].Add((j, w));
                adjacency[j].Add((i, w));
                diag[i] += w;
                diag[j] += w;
            }

            // 2. 添加正则化
            float h = heatFactor;
            for (int i = 0; i < nVerts; i++)
                diag[i] += h;

            // 3. 对每根骨骼求解热扩散
            for (int boneIdx = 0; boneIdx < nBones; boneIdx++)
            {
                var bone = rig.Bones[boneIdx];

                // 设置热源: 距离骨骼足够近的顶点
                var b = new float[nVerts];
                float boneLength = Math.Max(bone.Length, 1e-4f);
                float threshold = boneLength * 0.5f;

                for (int vi = 0; vi < nVerts; vi++)
                {
                    float dist = PointToSegmentDistance(positions[vi], bone.HeadPosition, bone.TailPosition);
                    if (dist < threshold)
                        b[vi] = 1f;
                }

                // 检查是否有热源
                bool hasSource = b.Any(v => v > 0f);
                if (!hasSource) continue;

                // Gauss-Seidel 求解 L' * w = b
                var w = SolveGaussSeidel(adjacency, diag, b, maxIterations);

                // 写入权重
                for (int vi = 0; vi < nVerts; vi++)
                {
                    if (w[vi] > 1e-6f)
                        weights.SetWeight(vi, boneIdx, w[vi]);
                }
            }

            // 4. 归一化
            weights.NormalizeAll();

            return weights;
        }

        // ──────────────────────────────────────────────────────────────
        //  VoxelWeights — 体素化权重
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 体素化权重计算 (更稳定的热扩散替代方案)。
        /// 1. 将网格体素化
        /// 2. 在体素空间计算每根骨骼的距离场
        /// 3. 将距离转换为权重
        /// 4. 从体素插值到顶点
        /// </summary>
        public static SkinWeights VoxelWeights(
            List<Vector3> positions, List<(int A, int B, int C)> triangles,
            SkeletonRig rig, int resolution = 32)
        {
            int nVerts = positions.Count;
            int nBones = rig.Bones.Count;
            var weights = new SkinWeights();
            weights.Init(nVerts);

            if (nVerts == 0 || nBones == 0) return weights;

            // 1. 计算 AABB
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = max - min;
            // 稍微扩大 AABB 以避免边界问题
            min -= size * 0.05f;
            max += size * 0.05f;
            size = max - min;

            float cellSize = Math.Max(Math.Max(size.X, size.Y), size.Z) / resolution;
            int nx = Math.Max(1, (int)(size.X / cellSize) + 1);
            int ny = Math.Max(1, (int)(size.Y / cellSize) + 1);
            int nz = Math.Max(1, (int)(size.Z / cellSize) + 1);

            // 2. 体素化: 标记内部体素
            var voxelInside = new bool[nx, ny, nz];
            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
            {
                var pos = new Vector3(
                    min.X + ix * cellSize,
                    min.Y + iy * cellSize,
                    min.Z + iz * cellSize);
                voxelInside[ix, iy, iz] = IsPointInsideMesh(pos, positions, triangles);
            }

            // 3. 对每根骨骼计算体素权重
            var voxelWeights = new float[nx, ny, nz, nBones];
            for (int boneIdx = 0; boneIdx < nBones; boneIdx++)
            {
                var bone = rig.Bones[boneIdx];
                for (int ix = 0; ix < nx; ix++)
                for (int iy = 0; iy < ny; iy++)
                for (int iz = 0; iz < nz; iz++)
                {
                    if (!voxelInside[ix, iy, iz]) continue;

                    var pos = new Vector3(
                        min.X + ix * cellSize,
                        min.Y + iy * cellSize,
                        min.Z + iz * cellSize);

                    float dist = PointToSegmentDistance(pos, bone.HeadPosition, bone.TailPosition);
                    // 线性衰减权重
                    float maxDist = bone.Length * 2f;
                    if (maxDist < 1e-4f) maxDist = 0.1f;
                    voxelWeights[ix, iy, iz, boneIdx] = Math.Max(0f, 1f - dist / maxDist);
                }
            }

            // 4. 归一化体素权重
            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
            {
                if (!voxelInside[ix, iy, iz]) continue;

                float sum = 0f;
                for (int b = 0; b < nBones; b++)
                    sum += voxelWeights[ix, iy, iz, b];

                if (sum > 1e-8f)
                    for (int b = 0; b < nBones; b++)
                        voxelWeights[ix, iy, iz, b] /= sum;
            }

            // 5. 三线性插值到顶点
            for (int vi = 0; vi < nVerts; vi++)
            {
                var p = positions[vi];
                float fx = (p.X - min.X) / cellSize;
                float fy = (p.Y - min.Y) / cellSize;
                float fz = (p.Z - min.Z) / cellSize;

                int ix0 = Math.Clamp((int)fx, 0, nx - 1);
                int iy0 = Math.Clamp((int)fy, 0, ny - 1);
                int iz0 = Math.Clamp((int)fz, 0, nz - 1);
                int ix1 = Math.Min(ix0 + 1, nx - 1);
                int iy1 = Math.Min(iy0 + 1, ny - 1);
                int iz1 = Math.Min(iz0 + 1, nz - 1);

                float tx = Math.Clamp(fx - ix0, 0f, 1f);
                float ty = Math.Clamp(fy - iy0, 0f, 1f);
                float tz = Math.Clamp(fz - iz0, 0f, 1f);

                for (int b = 0; b < nBones; b++)
                {
                    // 三线性插值
                    float w000 = voxelWeights[ix0, iy0, iz0, b];
                    float w001 = voxelWeights[ix0, iy0, iz1, b];
                    float w010 = voxelWeights[ix0, iy1, iz0, b];
                    float w011 = voxelWeights[ix0, iy1, iz1, b];
                    float w100 = voxelWeights[ix1, iy0, iz0, b];
                    float w101 = voxelWeights[ix1, iy0, iz1, b];
                    float w110 = voxelWeights[ix1, iy1, iz0, b];
                    float w111 = voxelWeights[ix1, iy1, iz1, b];

                    float wx00 = w000 * (1 - tx) + w100 * tx;
                    float wx01 = w001 * (1 - tx) + w101 * tx;
                    float wx10 = w010 * (1 - tx) + w110 * tx;
                    float wx11 = w011 * (1 - tx) + w111 * tx;

                    float wy0 = wx00 * (1 - ty) + wx10 * ty;
                    float wy1 = wx01 * (1 - ty) + wx11 * ty;

                    float w = wy0 * (1 - tz) + wy1 * tz;

                    if (w > 1e-6f)
                        weights.SetWeight(vi, b, w);
                }
            }

            weights.NormalizeAll();
            return weights;
        }

        // ──────────────────────────────────────────────────────────────
        //  辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>添加余切权重到边 (i, j), c 为对角顶点。</summary>
        private static void AddCotangentWeight(Dictionary<(int, int), float> edgeWeights,
            int i, int j, int k, List<Vector3> positions)
        {
            var key = i < j ? (i, j) : (j, i);
            float cot = Cotangent(positions[i], positions[j], positions[k]);

            if (edgeWeights.ContainsKey(key))
                edgeWeights[key] += cot;
            else
                edgeWeights[key] = cot;
        }

        /// <summary>计算角 k 处的余切值: cot = (a·b) / |a×b|。</summary>
        private static float Cotangent(Vector3 a, Vector3 b, Vector3 c)
        {
            var ca = a - c;
            var cb = b - c;
            var cross = Vector3.Cross(ca, cb);
            float crossLen = cross.Length();
            if (crossLen < 1e-10f) return 0f;
            return Vector3.Dot(ca, cb) / crossLen;
        }

        /// <summary>Gauss-Seidel 迭代求解 L' * w = b。</summary>
        private static float[] SolveGaussSeidel(
            List<List<(int neighbor, float weight)>> adjacency,
            float[] diag, float[] b,
            int maxIterations, float tolerance = 1e-5f)
        {
            int n = b.Length;
            var w = new float[n];

            for (int iter = 0; iter < maxIterations; iter++)
            {
                float maxChange = 0f;
                for (int i = 0; i < n; i++)
                {
                    float oldVal = w[i];
                    float sum = 0f;
                    foreach (var (j, weight) in adjacency[i])
                        sum += weight * w[j];
                    w[i] = (b[i] + sum) / diag[i];
                    float change = Math.Abs(w[i] - oldVal);
                    if (change > maxChange) maxChange = change;
                }
                if (maxChange < tolerance) break;
            }

            return w;
        }

        /// <summary>计算点到线段的距离。</summary>
        public static float PointToSegmentDistance(Vector3 point, Vector3 segStart, Vector3 segEnd)
        {
            var seg = segEnd - segStart;
            float segLenSq = seg.LengthSquared();
            if (segLenSq < 1e-10f) return Vector3.Distance(point, segStart);

            float t = Math.Clamp(Vector3.Dot(point - segStart, seg) / segLenSq, 0f, 1f);
            var closest = segStart + t * seg;
            return Vector3.Distance(point, closest);
        }

        /// <summary>使用射线投射判断点是否在网格内部 (奇偶规则)。</summary>
        public static bool IsPointInsideMesh(Vector3 point, List<Vector3> positions, List<(int A, int B, int C)> triangles)
        {
            // 向 +X 方向投射射线, 计算交点数
            var rayDir = Vector3.UnitX;
            int hitCount = 0;

            foreach (var (a, b, c) in triangles)
            {
                if (RayTriangleIntersect(point, rayDir, positions[a], positions[b], positions[c]))
                    hitCount++;
            }

            return hitCount % 2 == 1;
        }

        /// <summary>射线-三角形相交检测 (Moller-Trumbore 算法)。</summary>
        private static bool RayTriangleIntersect(Vector3 origin, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2)
        {
            const float EPS = 1e-8f;
            var edge1 = v1 - v0;
            var edge2 = v2 - v0;

            var h = Vector3.Cross(dir, edge2);
            float a = Vector3.Dot(edge1, h);
            if (a > -EPS && a < EPS) return false;

            float f = 1f / a;
            var s = origin - v0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f) return false;

            float t = f * Vector3.Dot(edge2, q);
            return t > EPS;
        }
    }

    // =================================================================================
    //  WeightBrushMode — 权重笔刷模式
    // =================================================================================

    /// <summary>权重绘制笔刷模式。</summary>
    public enum WeightBrushMode
    {
        Add,
        Subtract,
        Smooth,
        Normalize
    }

    // =================================================================================
    //  WeightPainting — 权重绘制工具
    // =================================================================================

    /// <summary>
    /// 权重绘制工具 —— 类似 Blender Weight Paint 模式。
    /// 支持笔刷式权重编辑 (Add / Subtract / Smooth / Normalize)。
    /// </summary>
    public class WeightPainting
    {
        /// <summary>笔刷中心 (世界空间)。</summary>
        public Vector3 Center = Vector3.Zero;

        /// <summary>笔刷半径。</summary>
        public float Radius = 0.5f;

        /// <summary>笔刷强度 (0~1)。</summary>
        public float Strength = 0.5f;

        /// <summary>当前正在绘制的骨骼索引。</summary>
        public int BoneIndex = 0;

        /// <summary>笔刷模式。</summary>
        public WeightBrushMode Mode = WeightBrushMode.Add;

        /// <summary>衰减曲线类型。</summary>
        public BrushFalloff Falloff = BrushFalloff.Smooth;

        // ──────────────────────────────────────────────────────────────
        //  Apply — 应用笔刷
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 应用权重笔刷到网格。
        /// 需要顶点位置列表、权重数据、邻接表 (用于 Smooth)。
        /// </summary>
        public void Apply(List<Vector3> positions, SkinWeights weights, List<HashSet<int>> adjacency)
        {
            if (positions == null || weights == null) return;

            // 找到笔刷影响范围内的顶点
            var affected = new List<(int index, float falloff)>();
            float radiusSq = Radius * Radius;

            for (int i = 0; i < positions.Count; i++)
            {
                float distSq = Vector3.DistanceSquared(positions[i], Center);
                if (distSq > radiusSq) continue;

                float dist = MathF.Sqrt(distSq);
                float normalizedDist = dist / Radius;
                float falloff = BrushFalloffCurve.Evaluate(normalizedDist, Falloff);
                affected.Add((i, falloff));
            }

            if (affected.Count == 0) return;

            switch (Mode)
            {
                case WeightBrushMode.Add:
                    PaintAdd(weights, affected);
                    break;
                case WeightBrushMode.Subtract:
                    PaintSubtract(weights, affected);
                    break;
                case WeightBrushMode.Smooth:
                    PaintSmooth(weights, affected, adjacency);
                    break;
                case WeightBrushMode.Normalize:
                    PaintNormalize(weights, affected);
                    break;
            }
        }

        private void PaintAdd(SkinWeights weights, List<(int index, float falloff)> affected)
        {
            foreach (var (vi, falloff) in affected)
            {
                float delta = Strength * falloff;
                weights.AddWeight(vi, BoneIndex, delta);
                weights.NormalizeVertex(vi);
            }
        }

        private void PaintSubtract(SkinWeights weights, List<(int index, float falloff)> affected)
        {
            foreach (var (vi, falloff) in affected)
            {
                float delta = -Strength * falloff;
                weights.AddWeight(vi, BoneIndex, delta);
                weights.NormalizeVertex(vi);
            }
        }

        private void PaintSmooth(SkinWeights weights, List<(int index, float falloff)> affected, List<HashSet<int>> adjacency)
        {
            if (adjacency == null) return;
            foreach (var (vi, falloff) in affected)
            {
                if (vi >= adjacency.Count || adjacency[vi].Count == 0) continue;

                // 获取所有相关骨骼
                var allBones = new HashSet<int>(weights.GetInfluencingBones(vi));
                foreach (var nb in adjacency[vi])
                    foreach (var b in weights.GetInfluencingBones(nb))
                        allBones.Add(b);

                // 对每根骨骼平滑
                foreach (var bone in allBones)
                {
                    float current = weights.GetWeight(vi, bone);
                    float sum = current;
                    int count = 1;

                    foreach (var nb in adjacency[vi])
                    {
                        sum += weights.GetWeight(nb, bone);
                        count++;
                    }

                    float smoothed = sum / count;
                    float blended = MathHelper.Lerp(current, smoothed, Strength * falloff);
                    weights.SetWeight(vi, bone, blended);
                }

                weights.NormalizeVertex(vi);
            }
        }

        private void PaintNormalize(SkinWeights weights, List<(int index, float falloff)> affected)
        {
            foreach (var (vi, falloff) in affected)
            {
                float blend = Strength * falloff;
                if (blend < 1f)
                {
                    // 部分归一化
                    var bones = weights.GetInfluencingBones(vi);
                    float sum = weights.GetTotalWeight(vi);
                    if (sum < 1e-8f) continue;

                    foreach (var bone in bones)
                    {
                        float current = weights.GetWeight(vi, bone);
                        float target = current / sum;
                        weights.SetWeight(vi, bone, MathHelper.Lerp(current, target, blend));
                    }
                }
                weights.NormalizeVertex(vi);
            }
        }

        /// <summary>获取权重可视化颜色 (红色=高权重, 蓝色=低权重)。</summary>
        public static Vector4 GetWeightColor(float weight)
        {
            // 蓝色 (0) -> 青色 -> 绿色 -> 黄色 -> 红色 (1)
            float t = Math.Clamp(weight, 0f, 1f);
            if (t < 0.25f)
                return new Vector4(0f, 0f, 0.5f + t * 2f, 1f); // 深蓝到蓝
            if (t < 0.5f)
                return new Vector4(0f, (t - 0.25f) * 4f, 1f, 1f); // 蓝到青
            if (t < 0.75f)
                return new Vector4((t - 0.5f) * 4f, 1f, 1f - (t - 0.5f) * 4f, 1f); // 青到黄
            return new Vector4(1f, 1f - (t - 0.75f) * 4f, 0f, 1f); // 黄到红
        }
    }

    // =================================================================================
    //  SkinningValidator — 蒙皮验证
    // =================================================================================

    /// <summary>
    /// 蒙皮验证工具 —— 检查权重和骨骼层级的完整性。
    /// </summary>
    public static class SkinningValidator
    {
        /// <summary>执行完整验证, 返回所有问题列表。</summary>
        public static List<string> Validate(SkinWeights weights, SkeletonRig rig, int vertexCount)
        {
            var errors = new List<string>();

            // 权重缺失检测
            var missing = FindMissingWeights(weights, vertexCount);
            if (missing.Count > 0)
                errors.Add($"发现 {missing.Count} 个无权重顶点: [{string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? "..." : "")}]");

            // 权重过多检测
            var excessive = FindExcessiveInfluences(weights, 4);
            if (excessive.Count > 0)
                errors.Add($"发现 {excessive.Count} 个顶点影响骨骼数 > 4: [{string.Join(", ", excessive.Take(10))}{(excessive.Count > 10 ? "..." : "")}]");

            // 权重未归一化检测
            var unnormalized = FindUnnormalizedWeights(weights, vertexCount);
            if (unnormalized.Count > 0)
                errors.Add($"发现 {unnormalized.Count} 个顶点权重未归一化 (总和不等于 1)。");

            // 骨骼层级验证
            var rigErrors = ValidateRig(rig);
            errors.AddRange(rigErrors);

            return errors;
        }

        /// <summary>查找无权重顶点 (总权重 = 0)。</summary>
        public static List<int> FindMissingWeights(SkinWeights weights, int vertexCount)
        {
            var missing = new List<int>();
            for (int i = 0; i < vertexCount; i++)
            {
                if (weights.GetTotalWeight(i) < 1e-6f)
                    missing.Add(i);
            }
            return missing;
        }

        /// <summary>查找影响骨骼过多的顶点 (influenceCount > maxInfluences)。</summary>
        public static List<int> FindExcessiveInfluences(SkinWeights weights, int maxInfluences = 4)
        {
            var excessive = new List<int>();
            for (int i = 0; i < weights.VertexWeights.Count; i++)
            {
                if (weights.GetInfluenceCount(i) > maxInfluences)
                    excessive.Add(i);
            }
            return excessive;
        }

        /// <summary>查找权重未归一化的顶点 (总权重 ≠ 1)。</summary>
        public static List<int> FindUnnormalizedWeights(SkinWeights weights, int vertexCount, float tolerance = 0.01f)
        {
            var unnormalized = new List<int>();
            for (int i = 0; i < vertexCount; i++)
            {
                float sum = weights.GetTotalWeight(i);
                if (sum > 1e-6f && Math.Abs(sum - 1f) > tolerance)
                    unnormalized.Add(i);
            }
            return unnormalized;
        }

        /// <summary>验证骨骼层级完整性。</summary>
        public static List<string> ValidateRig(SkeletonRig rig)
        {
            var errors = new List<string>();
            if (rig.Bones.Count == 0)
            {
                errors.Add("骨骼层级为空 (没有骨骼)。");
                return errors;
            }

            if (rig.RootBones.Count == 0)
                errors.Add("骨骼层级没有根骨骼。");

            if (rig.RootBones.Count > 1)
                errors.Add($"骨骼层级有 {rig.RootBones.Count} 个根骨骼 (通常应为 1)。");

            // 检查每个骨骼的父索引有效性
            for (int i = 0; i < rig.Bones.Count; i++)
            {
                var bone = rig.Bones[i];
                if (bone.Parent >= rig.Bones.Count)
                    errors.Add($"骨骼 '{bone.Name}' 的父索引 {bone.Parent} 超出范围。");
                if (bone.Parent == i)
                    errors.Add($"骨骼 '{bone.Name}' 的父索引指向自身。");
            }

            // 检查循环依赖
            if (HasCycle(rig))
                errors.Add("骨骼层级存在循环依赖。");

            return errors;
        }

        /// <summary>检测骨骼层级中的循环依赖。</summary>
        private static bool HasCycle(SkeletonRig rig)
        {
            var visited = new int[rig.Bones.Count]; // 0=未访问, 1=正在访问, 2=已完成

            foreach (var rootIdx in rig.RootBones)
            {
                if (HasCycleDFS(rig, rootIdx, visited))
                    return true;
            }

            // 检查所有骨骼 (防止非根骨骼形成独立循环)
            for (int i = 0; i < rig.Bones.Count; i++)
            {
                if (visited[i] == 0 && HasCycleDFS(rig, i, visited))
                    return true;
            }

            return false;
        }

        private static bool HasCycleDFS(SkeletonRig rig, int boneIdx, int[] visited)
        {
            if (boneIdx < 0 || boneIdx >= visited.Length) return false;
            if (visited[boneIdx] == 1) return true;  // 发现循环
            if (visited[boneIdx] == 2) return false;  // 已完成

            visited[boneIdx] = 1;

            foreach (var childIdx in rig.Bones[boneIdx].Children)
            {
                if (HasCycleDFS(rig, childIdx, visited))
                    return true;
            }

            visited[boneIdx] = 2;
            return false;
        }

        /// <summary>自动修复权重: 补全缺失权重, 限制影响数, 归一化。</summary>
        public static void AutoFix(SkinWeights weights, int vertexCount, int maxInfluences = 4)
        {
            // 归一化所有顶点
            weights.NormalizeAll();

            // 限制每顶点最大影响数
            for (int i = 0; i < vertexCount; i++)
            {
                while (weights.GetInfluenceCount(i) > maxInfluences)
                {
                    // 找到权重最小的骨骼并移除
                    var bones = weights.VertexWeights[i]
                        .OrderBy(kvp => kvp.Value)
                        .ToList();
                    if (bones.Count > 0)
                        weights.SetWeight(i, bones[0].Key, 0f);
                }
                weights.NormalizeVertex(i);
            }
        }
    }

    // =================================================================================
    //  MathHelper — 数学辅助
    // =================================================================================

    /// <summary>数学辅助函数。</summary>
}
