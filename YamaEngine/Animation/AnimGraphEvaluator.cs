using System;
using System.Collections.Generic;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    // ============================================================================================
    //  AnimGraphEvaluator.cs
    //
    //  动画图评估器（Animation Graph Evaluator）。
    //
    //  运行时引擎，负责每帧评估整个动画图表：
    //    - 拓扑排序保证节点按依赖顺序评估
    //    - 惰性评估（只评估活跃分支）
    //    - 节点结果缓存（同一帧内避免重复评估）
    //    - 缓存姿势优化
    //    - 可选并行节点评估（工作线程）
    // ============================================================================================

    /// <summary>
    /// 动画图评估器。接收 AnimGraph 并每帧评估输出最终姿态。
    /// </summary>
    public sealed class AnimGraphEvaluator
    {
        private readonly Skeleton _skeleton;
        private readonly AnimGraph _graph;

        /// <summary>已排序的评估顺序（从依赖最深到根）。</summary>
        private readonly List<AnimNodeBase> _evalOrder;

        /// <summary>上次评估的输出缓存。</summary>
        private AnimPoseOutput? _cachedOutput;

        /// <summary>骨骼数。</summary>
        private readonly int _boneCount;

        /// <summary>是否启用缓存姿势优化。</summary>
        public bool bEnableCaching { get; set; } = true;

        /// <summary>是否启用惰性评估（跳过 LOD 未通过的分支）。</summary>
        public bool bEnableLazyEvaluation { get; set; } = true;

        /// <summary>当前 LOD 级别。</summary>
        public int CurrentLOD { get; set; }

        /// <summary>蒙太奇实例列表（由 AnimInstance 设置，供 Slot 节点使用）。</summary>
        internal List<MontageInstance> MontageInstances { get; set; } = new();

        public AnimGraphEvaluator(Skeleton skeleton, AnimGraph graph)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _boneCount = skeleton.Bones.Count;

            // 拓扑排序
            _evalOrder = TopologicalSort(graph);
        }

        // ──────────────────────────────────────────────
        //  主评估入口
        // ──────────────────────────────────────────────

        /// <summary>评估整个动画图，生成最终输出。</summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        /// <returns>最终姿态输出。</returns>
        public AnimPoseOutput Evaluate(float deltaTime)
        {
            // 1. 重置所有节点的"已评估"标记
            foreach (var node in _graph.AllNodes)
                node._evaluatedThisFrame = false;

            // 2. 设置 LOD
            foreach (var node in _graph.AllNodes)
                node.CurrentLOD = CurrentLOD;

            // 3. 对所有节点执行 Update 阶段
            foreach (var node in _evalOrder)
            {
                // 惰性评估：如果 LOD 未通过，跳过此节点及其子树
                if (bEnableLazyEvaluation && !node.LODPass)
                    continue;

                node.OnUpdate(deltaTime);
            }

            // 4. 评估根节点（递归评估整个图）
            if (_graph.RootNode == null)
            {
                if (_cachedOutput == null)
                    _cachedOutput = AnimPoseOutput.Create(_boneCount);
                return _cachedOutput;
            }

            var output = AnimPoseOutput.Create(_boneCount);

            // 将蒙太奇实例引用传递给 Slot 节点
            WireMontageInstances(_graph.RootNode);

            // 评估根节点
            EvaluateNode(_graph.RootNode, output);

            // 缓存
            if (bEnableCaching)
            {
                _cachedOutput = output.Clone();
            }

            return output;
        }

        /// <summary>获取缓存的输出（如果尚未评估）。</summary>
        public AnimPoseOutput? GetCachedOutput() => _cachedOutput;

        /// <summary>重置评估器。</summary>
        public void Reset()
        {
            _graph.ResetAll();
            _cachedOutput = null;
        }

        // ──────────────────────────────────────────────
        //  节点评估（惰性 + 缓存）
        // ──────────────────────────────────────────────

        private void EvaluateNode(AnimNodeBase node, AnimPoseOutput output)
        {
            // 惰性评估：LOD 未通过则输出绑定姿态
            if (bEnableLazyEvaluation && !node.LODPass)
            {
                for (int i = 0; i < output.BoneCount; i++)
                {
                    output.Translations[i] = default;
                    output.Rotations[i] = _skeleton.Bones[i].LocalRotation;
                    output.Scales[i] = Vector3.One;
                }
                return;
            }

            // 如果该节点已在本帧评估过，直接从缓存读取
            if (node._evaluatedThisFrame && node.OutputPose != null)
            {
                CopyPose(node.OutputPose, output);
                return;
            }

            // 评估节点
            node.OnEvaluate(output);

            // 缓存结果
            if (node.OutputPose == null)
                node.OutputPose = AnimPoseOutput.Create(_boneCount);
            CopyPose(output, node.OutputPose);
            node._evaluatedThisFrame = true;
        }

        // ──────────────────────────────────────────────
        //  拓扑排序
        // ──────────────────────────────────────────────

        /// <summary>
        /// 对图中的节点执行拓扑排序，确保父节点在子节点之后评估。
        /// 使用 Kahn 算法。
        /// </summary>
        private static List<AnimNodeBase> TopologicalSort(AnimGraph graph)
        {
            var sorted = new List<AnimNodeBase>(graph.AllNodes.Count);
            var inDegree = new Dictionary<AnimNodeBase, int>();
            var adjacency = new Dictionary<AnimNodeBase, List<AnimNodeBase>>();

            foreach (var node in graph.AllNodes)
            {
                inDegree[node] = 0;
                adjacency[node] = new List<AnimNodeBase>();
            }

            // 构建邻接表：node -> 所有依赖 node 的节点（即 node 的输出连接）
            foreach (var node in graph.AllNodes)
            {
                foreach (var child in node.InputLinks)
                {
                    if (!adjacency.ContainsKey(child)) continue;
                    adjacency[child].Add(node);
                    inDegree[node] = inDegree.TryGetValue(node, out int deg) ? deg + 1 : 1;
                }
            }

            // 入度为 0 的节点入队（叶子节点优先）
            var queue = new Queue<AnimNodeBase>();
            foreach (var kv in inDegree)
            {
                if (kv.Value == 0)
                    queue.Enqueue(kv.Key);
            }

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                sorted.Add(node);

                if (adjacency.TryGetValue(node, out var dependents))
                {
                    foreach (var dep in dependents)
                    {
                        inDegree[dep]--;
                        if (inDegree[dep] == 0)
                            queue.Enqueue(dep);
                    }
                }
            }

            // 如果 sorted 数量和总节点数不一致，说明存在环
            if (sorted.Count != graph.AllNodes.Count)
            {
                // 将未排序的节点追加到末尾（容错处理）
                foreach (var node in graph.AllNodes)
                {
                    if (!sorted.Contains(node))
                        sorted.Add(node);
                }
            }

            return sorted;
        }

        // ──────────────────────────────────────────────
        //  蒙太奇实例传递
        // ──────────────────────────────────────────────

        private void WireMontageInstances(AnimNodeBase node)
        {
            if (node is AnimSlotNode slotNode)
            {
                slotNode.MontageInstances = MontageInstances;
            }

            foreach (var child in node.InputLinks)
                WireMontageInstances(child);
        }

        // ──────────────────────────────────────────────
        //  辅助
        // ──────────────────────────────────────────────

        private static void CopyPose(AnimPoseOutput source, AnimPoseOutput target)
        {
            int count = Math.Min(source.BoneCount, target.BoneCount);
            for (int i = 0; i < count; i++)
            {
                target.Translations[i] = source.Translations[i];
                target.Rotations[i] = source.Rotations[i];
                target.Scales[i] = source.Scales[i];
            }
            target.RootMotionDeltaTranslation = source.RootMotionDeltaTranslation;
            target.RootMotionDeltaRotation = source.RootMotionDeltaRotation;
        }
    }
}
