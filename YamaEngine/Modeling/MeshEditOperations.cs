using System.Numerics;

namespace YamaEngine.Modeling
{
    /// <summary>
    /// Professional polygon editing operations on <see cref="EditableMesh"/>.
    /// Implements Extrude, Inset, Bevel and Loop Cut similar to Blender / Maya / 3ds Max workflows.
    /// All operations mutate the mesh in place and return information about the new geometry.
    /// </summary>
    public static class MeshEditOperations
    {
        /// <summary>
        /// Extrudes a single triangular face along its normal by <paramref name="distance"/>.
        /// The original face is replaced by the cap (using new top vertices) and three side
        /// quads (each split into 2 triangles) connect the original ring to the new ring.
        /// Returns the indices of the newly created top vertices.
        /// </summary>
        public static ExtrudeResult ExtrudeFace(EditableMesh mesh, int faceIndex, float distance)
        {
            ValidateFaceIndex(mesh, faceIndex);
            var face = mesh.Faces[faceIndex];

            var a = mesh.Vertices[face.A];
            var b = mesh.Vertices[face.B];
            var c = mesh.Vertices[face.C];
            var normal = ComputeFaceNormal(a, b, c);
            var offset = normal * distance;

            // Create three new top vertices (duplicates of the ring shifted by offset).
            var topA = AddVertex(mesh, a + offset, GetUV(mesh, face.A));
            var topB = AddVertex(mesh, b + offset, GetUV(mesh, face.B));
            var topC = AddVertex(mesh, c + offset, GetUV(mesh, face.C));

            // Replace the original face with the new cap face (same winding to keep the normal).
            mesh.Faces[faceIndex] = new MeshFace(topA, topB, topC);

            // Side walls: for each edge (A-B, B-C, C-A) of the original ring, build a quad
            // [bot0, bot1, top1, top0] split into 2 triangles. Outward winding matches the
            // extrusion direction so the quad is visible from the outside.
            AddQuad(mesh, face.A, face.B, topB, topA);
            AddQuad(mesh, face.B, face.C, topC, topB);
            AddQuad(mesh, face.C, face.A, topA, topC);

            mesh.RecalculateNormals();
            return new ExtrudeResult(faceIndex, new[] { topA, topB, topC });
        }

        /// <summary>
        /// Inset (push inward) a triangular face. Creates a smaller similar triangle inside the
        /// original by interpolating each ring vertex toward the centroid by <paramref name="factor"/>
        /// (0 = no inset, 1 = collapse to centroid). The original face is replaced by the inner
        /// triangle and a "ring" of three quads fills the gap so the mesh stays closed.
        /// </summary>
        public static InsetResult InsetFace(EditableMesh mesh, int faceIndex, float factor)
        {
            ValidateFaceIndex(mesh, faceIndex);
            if (factor <= 0f || factor >= 1f) throw new ArgumentOutOfRangeException(nameof(factor), "Inset factor must be in (0,1).");
            var face = mesh.Faces[faceIndex];

            var a = mesh.Vertices[face.A];
            var b = mesh.Vertices[face.B];
            var c = mesh.Vertices[face.C];
            var centroid = (a + b + c) / 3f;

            var innerA = AddVertex(mesh, Vector3.Lerp(a, centroid, factor), GetUV(mesh, face.A));
            var innerB = AddVertex(mesh, Vector3.Lerp(b, centroid, factor), GetUV(mesh, face.B));
            var innerC = AddVertex(mesh, Vector3.Lerp(c, centroid, factor), GetUV(mesh, face.C));

            mesh.Faces[faceIndex] = new MeshFace(innerA, innerB, innerC);
            AddQuad(mesh, face.A, face.B, innerB, innerA);
            AddQuad(mesh, face.B, face.C, innerC, innerB);
            AddQuad(mesh, face.C, face.A, innerA, innerC);

            mesh.RecalculateNormals();
            return new InsetResult(faceIndex, new[] { innerA, innerB, innerC });
        }

        /// <summary>
        /// Bevel a single edge of a face by inserting two new vertices on the edge segment at
        /// <paramref name="amount"/> from each endpoint and creating a thin chamfer face.
        /// </summary>
        /// <remarks>
        /// This is a vertex-bevel approximation that works on isolated triangles: the original
        /// face is split into a chamfer triangle + a quad (2 tris) filling the remaining area.
        /// Suitable as a building block for more advanced edge-loop bevels in future passes.
        /// </remarks>
        public static BevelResult BevelEdge(EditableMesh mesh, int faceIndex, int edgeStart, int edgeEnd, float amount)
        {
            ValidateFaceIndex(mesh, faceIndex);
            if (amount <= 0f || amount >= 0.5f) throw new ArgumentOutOfRangeException(nameof(amount), "Bevel amount must be in (0,0.5).");
            var face = mesh.Faces[faceIndex];
            var ring = new[] { face.A, face.B, face.C };

            int startSlot = Array.IndexOf(ring, edgeStart);
            int endSlot = Array.IndexOf(ring, edgeEnd);
            if (startSlot < 0 || endSlot < 0 || (endSlot != (startSlot + 1) % 3 && startSlot != (endSlot + 1) % 3))
            {
                throw new ArgumentException("Edge does not belong to the requested face.");
            }
            // Normalize so that startSlot -> endSlot is the forward direction along the ring.
            if (endSlot != (startSlot + 1) % 3) (startSlot, endSlot) = (endSlot, startSlot);
            int oppositeSlot = (endSlot + 1) % 3;

            int iStart = ring[startSlot];
            int iEnd = ring[endSlot];
            int iOpposite = ring[oppositeSlot];

            var pStart = mesh.Vertices[iStart];
            var pEnd = mesh.Vertices[iEnd];

            int chamferA = AddVertex(mesh, Vector3.Lerp(pStart, pEnd, amount), GetUV(mesh, iStart));
            int chamferB = AddVertex(mesh, Vector3.Lerp(pStart, pEnd, 1f - amount), GetUV(mesh, iEnd));

            // Replace original face with three triangles preserving winding direction.
            mesh.Faces[faceIndex] = new MeshFace(iStart, chamferA, iOpposite);
            mesh.Faces.Add(new MeshFace(chamferA, chamferB, iOpposite));
            mesh.Faces.Add(new MeshFace(chamferB, iEnd, iOpposite));

            mesh.RecalculateNormals();
            return new BevelResult(new[] { chamferA, chamferB });
        }

        /// <summary>
        /// Insert a loop cut across a quad-strip described by two adjacent triangles that share
        /// the edge (sharedStart -> sharedEnd). Splits the shared edge at parameter t in (0,1)
        /// and rebuilds the four affected triangles so the strip becomes 4 smaller triangles
        /// (effectively a horizontal cut through the quad).
        /// </summary>
        public static LoopCutResult LoopCut(EditableMesh mesh, int faceIndexA, int faceIndexB, int sharedStart, int sharedEnd, float t = 0.5f)
        {
            ValidateFaceIndex(mesh, faceIndexA);
            ValidateFaceIndex(mesh, faceIndexB);
            if (faceIndexA == faceIndexB) throw new ArgumentException("Loop cut requires two distinct faces sharing an edge.");
            if (t <= 0f || t >= 1f) throw new ArgumentOutOfRangeException(nameof(t));

            if (!FaceContainsEdge(mesh.Faces[faceIndexA], sharedStart, sharedEnd) ||
                !FaceContainsEdge(mesh.Faces[faceIndexB], sharedStart, sharedEnd))
            {
                throw new ArgumentException("Both faces must contain the shared edge.");
            }

            int oppositeA = OppositeVertex(mesh.Faces[faceIndexA], sharedStart, sharedEnd);
            int oppositeB = OppositeVertex(mesh.Faces[faceIndexB], sharedStart, sharedEnd);

            var midpoint = Vector3.Lerp(mesh.Vertices[sharedStart], mesh.Vertices[sharedEnd], t);
            var midUV = Vector2.Lerp(GetUV(mesh, sharedStart), GetUV(mesh, sharedEnd), t);
            int mid = AddVertex(mesh, midpoint, midUV);

            return RebuildLoopCut(mesh, faceIndexA, faceIndexB, sharedStart, sharedEnd, mid, oppositeA, oppositeB);
        }

        /// <summary>
        /// Bridge two open edges (each defined by a pair of vertex indices) with a strip of quads.
        /// Creates a quad wall connecting edge1 [a0,a1] to edge2 [b0,b1]. The two edges should be
        /// boundary edges (belonging to exactly one face each). Produces 2 triangles per quad.
        /// </summary>
        public static BridgeResult BridgeEdges(EditableMesh mesh, int a0, int a1, int b0, int b1)
        {
            ValidateVertexIndex(mesh, a0); ValidateVertexIndex(mesh, a1);
            ValidateVertexIndex(mesh, b0); ValidateVertexIndex(mesh, b1);
            if (a0 == a1 || b0 == b1) throw new ArgumentException("Edge endpoints must differ.");

            // Create a single quad bridge [a0, a1, b1, b0] split into 2 triangles.
            // Winding is chosen so the quad faces outward.
            AddQuad(mesh, a0, a1, b1, b0);
            mesh.RecalculateNormals();
            return new BridgeResult(new[] { a0, a1, b1, b0 });
        }

        /// <summary>
        /// Bridge two boundary loops (lists of vertex indices forming open loops) with a tube of
        /// quads. Loop A and Loop B must have the same vertex count. Produces N quads (2N triangles).
        /// </summary>
        public static BridgeResult BridgeLoops(EditableMesh mesh, IReadOnlyList<int> loopA, IReadOnlyList<int> loopB)
        {
            if (loopA == null || loopB == null) throw new ArgumentNullException();
            if (loopA.Count != loopB.Count) throw new ArgumentException("Loops must have equal vertex count.");
            if (loopA.Count < 2) throw new ArgumentException("Loops must have at least 2 vertices.");

            for (int i = 0; i < loopA.Count; i++)
            {
                ValidateVertexIndex(mesh, loopA[i]);
                ValidateVertexIndex(mesh, loopB[i]);
            }

            var inserted = new List<int>();
            int n = loopA.Count;
            for (int i = 0; i < n; i++)
            {
                int a0 = loopA[i];
                int a1 = loopA[(i + 1) % n];
                int b0 = loopB[i];
                int b1 = loopB[(i + 1) % n];
                AddQuad(mesh, a0, a1, b1, b0);
                inserted.AddRange(new[] { a0, a1, b1, b0 });
            }

            mesh.RecalculateNormals();
            return new BridgeResult(inserted.ToArray());
        }

        /// <summary>
        /// Merge multiple vertices into a single vertex at the average (or specified) position.
        /// All faces referencing any of the merged vertices are updated to reference the kept vertex.
        /// Degenerate faces (where two or more vertices collapse to the same index) are removed.
        /// </summary>
        public static MergeVerticesResult MergeVertices(EditableMesh mesh, IReadOnlyList<int> vertexIndices, Vector3? targetPosition = null)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (vertexIndices == null || vertexIndices.Count < 2)
                throw new ArgumentException("At least two vertices are required to merge.");

            int keepIndex = vertexIndices[0];
            foreach (var idx in vertexIndices)
                ValidateVertexIndex(mesh, idx);

            // Compute target position (average by default)
            Vector3 pos = targetPosition ?? ComputeAveragePosition(mesh, vertexIndices);
            mesh.Vertices[keepIndex] = pos;

            // Build remap: all merged vertices point to keepIndex
            var remap = new Dictionary<int, int>();
            for (int i = 1; i < vertexIndices.Count; i++)
                remap[vertexIndices[i]] = keepIndex;

            // Remap faces and remove degenerate ones
            int removedFaces = 0;
            for (int i = mesh.Faces.Count - 1; i >= 0; i--)
            {
                var f = mesh.Faces[i];
                int a = remap.TryGetValue(f.A, out var ra) ? ra : f.A;
                int b = remap.TryGetValue(f.B, out var rb) ? rb : f.B;
                int c = remap.TryGetValue(f.C, out var rc) ? rc : f.C;

                if (a == b || b == c || a == c)
                {
                    mesh.Faces.RemoveAt(i);
                    removedFaces++;
                }
                else
                {
                    mesh.Faces[i] = new MeshFace(a, b, c);
                }
            }

            mesh.RecalculateNormals();
            return new MergeVerticesResult(keepIndex, removedFaces);
        }

        /// <summary>
        /// Subdivide all triangular faces: each triangle is split into 4 smaller triangles by
        /// inserting midpoints on each edge. This is a Catmull-Clark-style linear subdivision
        /// that quadruples the triangle count while preserving shape.
        /// </summary>
        public static SubdivideResult Subdivide(EditableMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (mesh.Faces.Count == 0) return new SubdivideResult(0, 0);

            // Cache midpoint vertices to avoid duplicates on shared edges
            var edgeMidpoints = new Dictionary<(int, int), int>();
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;

            var newFaces = new List<MeshFace>(originalFaceCount * 4);

            for (int i = 0; i < originalFaceCount; i++)
            {
                var f = mesh.Faces[i];
                if (!f.IsValidFor(originalVertexCount)) { newFaces.Add(f); continue; }

                int midAB = GetOrCreateMidpoint(mesh, edgeMidpoints, f.A, f.B);
                int midBC = GetOrCreateMidpoint(mesh, edgeMidpoints, f.B, f.C);
                int midCA = GetOrCreateMidpoint(mesh, edgeMidpoints, f.C, f.A);

                // 4 sub-triangles: corner A, corner B, corner C, center
                newFaces.Add(new MeshFace(f.A, midAB, midCA));
                newFaces.Add(new MeshFace(midAB, f.B, midBC));
                newFaces.Add(new MeshFace(midCA, midBC, f.C));
                newFaces.Add(new MeshFace(midAB, midBC, midCA));
            }

            mesh.Faces = newFaces;
            mesh.RecalculateNormals();

            return new SubdivideResult(
                mesh.Vertices.Count - originalVertexCount,
                mesh.Faces.Count - originalFaceCount);
        }

        /// <summary>
        /// Subdivide a single face into 4 sub-triangles.
        /// </summary>
        public static SubdivideResult SubdivideFace(EditableMesh mesh, int faceIndex)
        {
            ValidateFaceIndex(mesh, faceIndex);
            var f = mesh.Faces[faceIndex];
            var edgeMidpoints = new Dictionary<(int, int), int>();

            int midAB = GetOrCreateMidpoint(mesh, edgeMidpoints, f.A, f.B);
            int midBC = GetOrCreateMidpoint(mesh, edgeMidpoints, f.B, f.C);
            int midCA = GetOrCreateMidpoint(mesh, edgeMidpoints, f.C, f.A);

            // Replace original face with center triangle
            mesh.Faces[faceIndex] = new MeshFace(midAB, midBC, midCA);
            // Add 3 corner triangles
            mesh.Faces.Add(new MeshFace(f.A, midAB, midCA));
            mesh.Faces.Add(new MeshFace(midAB, f.B, midBC));
            mesh.Faces.Add(new MeshFace(midCA, midBC, f.C));

            mesh.RecalculateNormals();
            return new SubdivideResult(3, 3);
        }

        // ---------------------------------------------------------------- helpers

        private static LoopCutResult RebuildLoopCut(EditableMesh mesh, int faceIndexA, int faceIndexB,
            int sharedStart, int sharedEnd, int mid, int oppositeA, int oppositeB)
        {
            var orderedA = OrderRingForwards(mesh.Faces[faceIndexA], sharedStart, sharedEnd);
            mesh.Faces[faceIndexA] = new MeshFace(orderedA.start, mid, orderedA.opposite);
            mesh.Faces.Add(new MeshFace(mid, orderedA.end, orderedA.opposite));

            var orderedB = OrderRingForwards(mesh.Faces[faceIndexB], sharedStart, sharedEnd);
            mesh.Faces[faceIndexB] = new MeshFace(orderedB.start, mid, orderedB.opposite);
            mesh.Faces.Add(new MeshFace(mid, orderedB.end, orderedB.opposite));

            mesh.RecalculateNormals();
            return new LoopCutResult(mid);
        }

        private static (int start, int end, int opposite) OrderRingForwards(MeshFace face, int sharedStart, int sharedEnd)
        {
            var ring = new[] { face.A, face.B, face.C };
            for (var i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                int k = (i + 2) % 3;
                if (ring[i] == sharedStart && ring[j] == sharedEnd) return (ring[i], ring[j], ring[k]);
                if (ring[i] == sharedEnd && ring[j] == sharedStart) return (ring[j], ring[i], ring[k]); // flipped winding
            }
            throw new InvalidOperationException("Face does not contain the shared edge.");
        }

        private static bool FaceContainsEdge(MeshFace face, int v0, int v1)
        {
            return (face.A == v0 || face.B == v0 || face.C == v0) && (face.A == v1 || face.B == v1 || face.C == v1) && v0 != v1;
        }

        private static int OppositeVertex(MeshFace face, int sharedStart, int sharedEnd)
        {
            foreach (var v in new[] { face.A, face.B, face.C })
            {
                if (v != sharedStart && v != sharedEnd) return v;
            }
            throw new InvalidOperationException("Face has no opposite vertex.");
        }

        private static void AddQuad(EditableMesh mesh, int v0, int v1, int v2, int v3)
        {
            mesh.Faces.Add(new MeshFace(v0, v1, v2));
            mesh.Faces.Add(new MeshFace(v0, v2, v3));
        }

        private static int AddVertex(EditableMesh mesh, Vector3 position, Vector2 uv)
        {
            mesh.Vertices.Add(position);
            mesh.UV0.Add(uv);
            mesh.Normals.Add(Vector3.UnitY);
            return mesh.Vertices.Count - 1;
        }

        private static Vector2 GetUV(EditableMesh mesh, int index)
        {
            return index >= 0 && index < mesh.UV0.Count ? mesh.UV0[index] : Vector2.Zero;
        }

        private static Vector3 ComputeFaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            return n.LengthSquared() > 0.000001f ? Vector3.Normalize(n) : Vector3.UnitY;
        }

        private static void ValidateFaceIndex(EditableMesh mesh, int faceIndex)
        {
            if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) throw new ArgumentOutOfRangeException(nameof(faceIndex));
        }

        private static void ValidateVertexIndex(EditableMesh mesh, int vertexIndex)
        {
            if (vertexIndex < 0 || vertexIndex >= mesh.Vertices.Count)
                throw new ArgumentOutOfRangeException(nameof(vertexIndex), $"Vertex index {vertexIndex} out of range [0,{mesh.Vertices.Count}).");
        }

        private static Vector3 ComputeAveragePosition(EditableMesh mesh, IReadOnlyList<int> indices)
        {
            Vector3 sum = Vector3.Zero;
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < mesh.Vertices.Count)
                    sum += mesh.Vertices[idx];
            }
            return indices.Count > 0 ? sum / indices.Count : Vector3.Zero;
        }

        private static int GetOrCreateMidpoint(EditableMesh mesh, Dictionary<(int, int), int> cache, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cache.TryGetValue(key, out int cached)) return cached;

            var pa = mesh.Vertices[a];
            var pb = mesh.Vertices[b];
            var mid = (pa + pb) * 0.5f;
            var midUV = (GetUV(mesh, a) + GetUV(mesh, b)) * 0.5f;
            int idx = AddVertex(mesh, mid, midUV);
            cache[key] = idx;
            return idx;
        }
    }

    public readonly record struct ExtrudeResult(int CapFaceIndex, int[] CapVertices);
    public readonly record struct InsetResult(int CapFaceIndex, int[] InnerVertices);
    public readonly record struct BevelResult(int[] InsertedVertices);
    public readonly record struct LoopCutResult(int InsertedVertex);
    public readonly record struct BridgeResult(int[] BridgeVertices);
    public readonly record struct MergeVerticesResult(int KeptVertexIndex, int RemovedFaceCount);
    public readonly record struct SubdivideResult(int AddedVertices, int AddedFaces);
}
