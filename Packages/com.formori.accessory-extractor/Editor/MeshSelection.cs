using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace AccessoryPrefabMaker
{
    public struct Face
    {
        public int a, b, c, submesh;
        public Face(int a, int b, int c, int submesh) { this.a = a; this.b = b; this.c = c; this.submesh = submesh; }
    }

    public static class MeshSelection
    {
        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skin) return skin.sharedMesh;
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
        }

        // World bounds of what is actually drawn. Renderer.bounds of an outfit mesh is often a 1 m default box,
        // which framed an 8 cm accessory from metres away in the preview.
        public static Bounds GeometryBounds(Renderer renderer)
        {
            var mesh = new Mesh();
            try
            {
                Vector3[] points;
                if (renderer is SkinnedMeshRenderer skin) { skin.BakeMesh(mesh, true); points = mesh.vertices; }
                else { var source = GetMesh(renderer); points = source && source.isReadable ? source.vertices : new Vector3[0]; }
                if (points.Length == 0) return renderer.bounds;
                var bounds = new Bounds(renderer.transform.TransformPoint(points[0]), Vector3.zero);
                foreach (var p in points) bounds.Encapsulate(renderer.transform.TransformPoint(p));
                return bounds;
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        public static Face[] Faces(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) throw new ArgumentException(ApmLocalization.T("메쉬를 읽을 수 없습니다. Read/Write 설정을 확인하세요."));
            var list = new List<Face>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) throw new NotSupportedException(ApmLocalization.T("삼각형 메쉬만 지원합니다."));
                var indices = mesh.GetTriangles(s);
                for (int i = 0; i < indices.Length; i += 3) list.Add(new Face(indices[i], indices[i + 1], indices[i + 2], s));
            }
            return list.ToArray();
        }

        public static int[] PickUV(Mesh mesh, Vector2 point, int slot = -1) { return PickUVFaces(Faces(mesh), mesh.uv, mesh.vertexCount, point, slot); }
        // Cached-topology overloads: the window keeps faces/UVs per source revision instead of rereading the mesh per click.
        public static int[] PickUVFaces(Face[] faces, Vector2[] uv, int vertexCount, Vector2 point, int slot = -1)
        {
            if (uv.Length != vertexCount) return new int[0];
            return faces.Select((f, i) => new { f, i }).Where(x => (slot < 0 || x.f.submesh == slot) && InTriangle(point, uv[x.f.a], uv[x.f.b], uv[x.f.c])).Select(x => x.i).ToArray();
        }

        public static int[] InUVRect(Mesh mesh, Rect rect, int slot = -1) { return InUVRectFaces(Faces(mesh), mesh.uv, mesh.vertexCount, rect, slot); }
        public static int[] InUVRectFaces(Face[] faces, Vector2[] uv, int vertexCount, Rect rect, int slot = -1)
        {
            if (uv.Length != vertexCount) return new int[0];
            rect = Rect.MinMaxRect(Mathf.Min(rect.xMin, rect.xMax), Mathf.Min(rect.yMin, rect.yMax), Mathf.Max(rect.xMin, rect.xMax), Mathf.Max(rect.yMin, rect.yMax));
            var corners = new[] { new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin), new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax) };
            var result = new List<int>();
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i]; if (slot >= 0 && slot != f.submesh) continue;
                var triangle = new[] { uv[f.a], uv[f.b], uv[f.c] };
                bool hit = triangle.Any(p => p.x >= rect.xMin && p.x <= rect.xMax && p.y >= rect.yMin && p.y <= rect.yMax) || corners.Any(p => InTriangle(p, triangle[0], triangle[1], triangle[2]));
                for (int a = 0; !hit && a < 3; a++) for (int b = 0; !hit && b < 4; b++) hit = SegmentsIntersect(triangle[a], triangle[(a + 1) % 3], corners[b], corners[(b + 1) % 4]);
                if (hit) result.Add(i);
            }
            return result.ToArray();
        }

        public static int RayPick(Mesh mesh, Ray ray, out float distance, int slot = -1)
        {
            var vertices = mesh.vertices; var faces = Faces(mesh); int best = -1; distance = float.PositiveInfinity;
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i]; if (slot >= 0 && f.submesh != slot) continue;
                var e1 = vertices[f.b] - vertices[f.a]; var e2 = vertices[f.c] - vertices[f.a];
                var p = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-10f) continue;
                var t = ray.origin - vertices[f.a]; float u = Vector3.Dot(t, p) / det;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(t, e1); float v = Vector3.Dot(ray.direction, q) / det;
                if (v < 0 || u + v > 1) continue;
                float d = Vector3.Dot(e2, q) / det;
                if (d >= 0 && d < distance) { distance = d; best = i; }
            }
            return best;
        }

        // UV islands join matching UV edges at the same 3D edge, never unrelated stacked islands.
        public static int[] Connected(Mesh mesh, int face, bool uv, int slot = -1)
        {
            var faces = Faces(mesh); if (face < 0 || face >= faces.Length) throw new ArgumentOutOfRangeException(nameof(face));
            if (slot >= 0 && faces[face].submesh != slot) return new int[0];
            var coords = mesh.uv; if (uv && coords.Length != mesh.vertexCount) return new int[0];
            var vertices = mesh.vertices;
            var links = new Dictionary<EdgeKey, List<int>>();
            var keys = new EdgeKey[faces.Length][];
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i]; if (slot >= 0 && f.submesh != slot) continue;
                int[] ids = { f.a, f.b, f.c }; keys[i] = new EdgeKey[3];
                for (int e = 0; e < 3; e++)
                {
                    int a = ids[e], b = ids[(e + 1) % 3];
                    var key = new EdgeKey(a, b, uv, vertices[a], vertices[b], uv ? coords[a] : Vector2.zero, uv ? coords[b] : Vector2.zero);
                    keys[i][e] = key;
                    if (!links.TryGetValue(key, out var neighbors)) links[key] = neighbors = new List<int>();
                    neighbors.Add(i);
                }
            }
            var found = new HashSet<int> { face }; var queue = new Queue<int>(); queue.Enqueue(face);
            while (queue.Count > 0)
            {
                foreach (var key in keys[queue.Dequeue()]) foreach (int next in links[key]) if (found.Add(next)) queue.Enqueue(next);
            }
            return found.OrderBy(i => i).ToArray();
        }

        // Same connectivity as Connected, for every face at once (union-find): label[i] is the island id,
        // -1 for faces outside the slot filter. One pass replaces one flood fill per seed face.
        public static int[] IslandLabels(Face[] faces, Vector3[] vertices, Vector2[] coords, bool uv, int slot = -1)
        {
            var labels = new int[faces.Length];
            if (uv && coords.Length != vertices.Length) { for (int i = 0; i < labels.Length; i++) labels[i] = -1; return labels; }
            var parent = new int[faces.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            Func<int, int> find = null;
            find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            var owner = new Dictionary<EdgeKey, int>();
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i]; if (slot >= 0 && f.submesh != slot) continue;
                int[] ids = { f.a, f.b, f.c };
                for (int e = 0; e < 3; e++)
                {
                    int a = ids[e], b = ids[(e + 1) % 3];
                    var key = new EdgeKey(a, b, uv, vertices[a], vertices[b], uv ? coords[a] : Vector2.zero, uv ? coords[b] : Vector2.zero);
                    if (owner.TryGetValue(key, out int other)) { int x = find(i), y = find(other); if (x != y) parent[x] = y; }
                    else owner[key] = i;
                }
            }
            for (int i = 0; i < faces.Length; i++) labels[i] = slot >= 0 && faces[i].submesh != slot ? -1 : find(i);
            return labels;
        }

        struct EdgeKey : IEquatable<EdgeKey>
        {
            readonly int a, b; readonly bool uv; readonly Vector3 pa, pb; readonly Vector2 ua, ub;
            public EdgeKey(int a, int b, bool uv, Vector3 pa, Vector3 pb, Vector2 ua, Vector2 ub) { this.a = a; this.b = b; this.uv = uv; this.pa = pa; this.pb = pb; this.ua = ua; this.ub = ub; }
            public bool Equals(EdgeKey o)
            {
                if (uv != o.uv) return false;
                // Imported UV/normal seams duplicate indices at the same geometric edge.
                if (!uv) return pa.Equals(o.pa) && pb.Equals(o.pb) || pa.Equals(o.pb) && pb.Equals(o.pa);
                return pa.Equals(o.pa) && pb.Equals(o.pb) && ua.Equals(o.ua) && ub.Equals(o.ub) || pa.Equals(o.pb) && pb.Equals(o.pa) && ua.Equals(o.ub) && ub.Equals(o.ua);
            }
            public override bool Equals(object obj) { return obj is EdgeKey key && Equals(key); }
            public override int GetHashCode() { return pa.GetHashCode() ^ pb.GetHashCode() ^ (uv ? ua.GetHashCode() ^ ub.GetHashCode() : 0); }
        }

        static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }
        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float area = Cross(b - a, c - a); if (Mathf.Abs(area) < 1e-12f) return false;
            float x = Cross(b - a, p - a), y = Cross(c - b, p - b), z = Cross(a - c, p - c);
            return x >= -1e-7f && y >= -1e-7f && z >= -1e-7f || x <= 1e-7f && y <= 1e-7f && z <= 1e-7f;
        }
        static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            var r = b - a; var s = d - c; float denominator = Cross(r, s);
            if (Mathf.Abs(denominator) < 1e-12f) return false;
            float t = Cross(c - a, s) / denominator, u = Cross(c - a, r) / denominator;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }

        public static string Fingerprint(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return "";
            using (var sha = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            // Buffered: per-value writes straight into CryptoStream took seconds on outfits with many shape keys.
            // The hashed bytes are unchanged, so recipes saved by earlier versions still match.
            using (var writer = new BinaryWriter(new BufferedStream(stream, 1 << 20)))
            {
                writer.Write(mesh.vertexCount); writer.Write(mesh.subMeshCount);
                // Hash raw streams to include every UV dimension, precision and vertex attribute.
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    for (int s = 0; s < data[0].vertexBufferCount; s++)
                    { var bytes = data[0].GetVertexData<byte>(s); writer.Write(bytes.Length); writer.Write(bytes.ToArray()); }
                    var indices = data[0].GetIndexData<byte>(); writer.Write(indices.Length); writer.Write(indices.ToArray());
                }
                foreach (var attribute in mesh.GetVertexAttributes()) { writer.Write((int)attribute.attribute); writer.Write((int)attribute.format); writer.Write(attribute.dimension); writer.Write(attribute.stream); }
                for (int s = 0; s < mesh.subMeshCount; s++) { var sub = mesh.GetSubMesh(s); writer.Write((int)sub.topology); writer.Write(sub.indexStart); writer.Write(sub.indexCount); writer.Write(sub.baseVertex); }
                foreach (var matrix in mesh.bindposes) for (int i = 0; i < 16; i++) writer.Write(matrix[i]);
                using (var counts = mesh.GetBonesPerVertex()) { writer.Write(counts.Length); writer.Write(counts.ToArray()); }
                using (var weights = mesh.GetAllBoneWeights()) { writer.Write(weights.Length); foreach (var w in weights) { writer.Write(w.boneIndex); writer.Write(w.weight); } }
                var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                writer.Write(mesh.blendShapeCount);
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    writer.Write(mesh.GetBlendShapeName(s)); writer.Write(mesh.GetBlendShapeFrameCount(s));
                    for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                    {
                        writer.Write(mesh.GetBlendShapeFrameWeight(s, f)); mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                        // Same bytes as writing x, y, z per vertex (little-endian float layout).
                        foreach (var array in new[] { dv, dn, dt }) writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(array)));
                    }
                }
                writer.Flush(); stream.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
