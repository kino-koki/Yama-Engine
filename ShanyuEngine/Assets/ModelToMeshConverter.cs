using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Assets.Importers;
using ShanyuEngine.Modeling;

namespace ShanyuEngine.Assets
{
    /// <summary>
    /// 将 DCC 导入的 <see cref="InterchangeScene"/> 转换为引擎可编辑的 <see cref="EditableMesh"/> 列表。
    /// </summary>
    public static class ModelToMeshConverter
    {
        /// <summary>
        /// 把 <paramref name="scene"/> 中的所有网格转换为 <see cref="EditableMesh"/>。
        /// 顶点会自动乘以其所属节点的世界变换矩阵。
        /// </summary>
        public static List<EditableMesh> Convert(InterchangeScene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            var result = new List<EditableMesh>();

            foreach (var mesh in scene.Meshes)
            {
                var editableMesh = new EditableMesh { Name = mesh.Name };

                // 复制顶点数据
                int vertexCount = mesh.Vertices.Count;
                for (int i = 0; i < vertexCount; i++)
                {
                    editableMesh.Vertices.Add(mesh.Vertices[i]);
                }

                // 复制法线（如果存在）
                if (mesh.Normals.Count == vertexCount)
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        editableMesh.Normals.Add(mesh.Normals[i]);
                    }
                }

                // 复制 UV（如果存在）
                if (mesh.UV0.Count == vertexCount)
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        editableMesh.UV0.Add(mesh.UV0[i]);
                    }
                }

                // 填充默认颜色（白色）
                for (int i = 0; i < vertexCount; i++)
                {
                    editableMesh.Colors.Add(Vector4.One);
                }

                // 复制索引（按三角形）
                for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    editableMesh.Faces.Add(new MeshFace(
                        (int)mesh.Indices[i],
                        (int)mesh.Indices[i + 1],
                        (int)mesh.Indices[i + 2]));
                }

                // 应用节点变换
                if (!string.IsNullOrEmpty(mesh.NodeId) &&
                    scene.Nodes.TryGetValue(mesh.NodeId, out var node))
                {
                    ApplyTransform(editableMesh, node.Transform);
                }

                // 如果缺少法线，重新计算
                if (editableMesh.Normals.Count != editableMesh.Vertices.Count)
                {
                    editableMesh.RecalculateNormals();
                }

                // 确保 UV0 存在（避免后续渲染出错）
                while (editableMesh.UV0.Count < editableMesh.Vertices.Count)
                {
                    editableMesh.UV0.Add(Vector2.Zero);
                }

                result.Add(editableMesh);
            }

            return result;
        }

        /// <summary>
        /// 将单个 <see cref="InterchangeMesh"/> 转换为 <see cref="EditableMesh"/>。
        /// </summary>
        public static EditableMesh ConvertMesh(InterchangeMesh mesh, Matrix4x4? transform = null)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var editableMesh = new EditableMesh { Name = mesh.Name };

            int vertexCount = mesh.Vertices.Count;
            for (int i = 0; i < vertexCount; i++)
                editableMesh.Vertices.Add(mesh.Vertices[i]);

            if (mesh.Normals.Count == vertexCount)
                for (int i = 0; i < vertexCount; i++)
                    editableMesh.Normals.Add(mesh.Normals[i]);

            if (mesh.UV0.Count == vertexCount)
                for (int i = 0; i < vertexCount; i++)
                    editableMesh.UV0.Add(mesh.UV0[i]);

            for (int i = 0; i < vertexCount; i++)
                editableMesh.Colors.Add(Vector4.One);

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                editableMesh.Faces.Add(new MeshFace(
                    (int)mesh.Indices[i],
                    (int)mesh.Indices[i + 1],
                    (int)mesh.Indices[i + 2]));
            }

            if (transform.HasValue)
                ApplyTransform(editableMesh, transform.Value);

            if (editableMesh.Normals.Count != editableMesh.Vertices.Count)
                editableMesh.RecalculateNormals();

            while (editableMesh.UV0.Count < editableMesh.Vertices.Count)
                editableMesh.UV0.Add(Vector2.Zero);

            return editableMesh;
        }

        /// <summary>
        /// 对 EditableMesh 的所有顶点、法线应用矩阵变换。
        /// </summary>
        private static void ApplyTransform(EditableMesh mesh, Matrix4x4 transform)
        {
            if (mesh == null || mesh.Vertices.Count == 0)
                return;

            // 提取旋转部分用于变换法线
            Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _);
            Matrix4x4 normalMatrix = Matrix4x4.CreateFromQuaternion(rotation);

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], transform);
            }

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                mesh.Normals[i] = Vector3.TransformNormal(mesh.Normals[i], normalMatrix);
                if (mesh.Normals[i].LengthSquared() > 1e-6f)
                    mesh.Normals[i] = Vector3.Normalize(mesh.Normals[i]);
            }
        }
    }
}
