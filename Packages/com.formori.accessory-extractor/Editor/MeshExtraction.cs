using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace AccessoryPrefabMaker
{
    public static class MeshExtraction
    {
        public static MeshSlice Extract(Mesh source, IList<int> selectedFaces, bool keepSkin)
        {
            if (source == null || !source.isReadable) throw new ArgumentException(ApmLocalization.T("읽을 수 있는 원본 메쉬가 필요합니다."));
            if (selectedFaces == null || selectedFaces.Count == 0) throw new ArgumentException(ApmLocalization.T("추출할 면을 선택하세요."));
            var faces = MeshSelection.Faces(source);
            var selected = selectedFaces.Distinct().OrderBy(i => i).ToArray();
            foreach (int i in selected) if (i < 0 || i >= faces.Length) throw new ArgumentOutOfRangeException(nameof(selectedFaces));
            var used = new SortedSet<int>();
            foreach (int i in selected) { used.Add(faces[i].a); used.Add(faces[i].b); used.Add(faces[i].c); }
            var vertices = used.ToArray();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < vertices.Length; i++) map.Add(vertices[i], i);
            var slots = selected.Select(i => faces[i].submesh).Distinct().OrderBy(i => i).ToArray();
            var result = new MeshSlice { mesh = new Mesh { name = source.name + "_Accessory", indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 }, sourceVertices = vertices, sourceSubmeshes = slots };
            try
            {
                var mesh = result.mesh;
                mesh.vertices = Select(source.vertices, vertices);
                if (source.HasVertexAttribute(VertexAttribute.Normal)) mesh.normals = Select(source.normals, vertices);
                if (source.HasVertexAttribute(VertexAttribute.Tangent)) mesh.tangents = Select(source.tangents, vertices);
                if (source.HasVertexAttribute(VertexAttribute.Color))
                {
                    if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8) mesh.colors32 = Select(source.colors32, vertices);
                    else mesh.colors = Select(source.colors, vertices);
                }
                for (int channel = 0; channel < 8; channel++)
                {
                    var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                    if (!source.HasVertexAttribute(attribute)) continue;
                    int dimension = source.GetVertexAttributeDimension(attribute);
                    // 1D UVs are stored as 2D (y=0); shaders reading .x see the same value.
                    if (dimension == 1) { var data = new List<Vector4>(); source.GetUVs(channel, data); mesh.SetUVs(channel, vertices.Select(i => new Vector2(data[i].x, 0)).ToList()); }
                    else if (dimension == 2) { var data = new List<Vector2>(); source.GetUVs(channel, data); mesh.SetUVs(channel, vertices.Select(i => data[i]).ToList()); }
                    else if (dimension == 3) { var data = new List<Vector3>(); source.GetUVs(channel, data); mesh.SetUVs(channel, vertices.Select(i => data[i]).ToList()); }
                    else if (dimension == 4) { var data = new List<Vector4>(); source.GetUVs(channel, data); mesh.SetUVs(channel, vertices.Select(i => data[i]).ToList()); }
                    else throw new NotSupportedException(ApmLocalization.T("지원하지 않는 UV 차원입니다: ") + "UV" + channel + " (" + dimension + ")");
                }
                mesh.subMeshCount = slots.Length;
                for (int s = 0; s < slots.Length; s++)
                {
                    var triangles = new List<int>();
                    foreach (int i in selected)
                    {
                        var face = faces[i]; if (face.submesh != slots[s]) continue;
                        triangles.Add(map[face.a]); triangles.Add(map[face.b]); triangles.Add(map[face.c]);
                    }
                    mesh.SetTriangles(triangles, s, false);
                }
                if (keepSkin)
                {
                    mesh.bindposes = source.bindposes;
                    CopyWeights(source, mesh, vertices);
                    CopyShapes(source, mesh, vertices);
                    // Keep conservative deformation bounds; the renderer's own localBounds is copied by the exporter.
                    mesh.bounds = source.bounds;
                }
                else mesh.RecalculateBounds();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        static T[] Select<T>(T[] input, int[] indices) { return indices.Select(i => input[i]).ToArray(); }

        static void CopyWeights(Mesh source, Mesh target, int[] vertices)
        {
            using (var counts = source.GetBonesPerVertex())
            using (var weights = source.GetAllBoneWeights())
            {
                if (weights.Length == 0) return;
                if (counts.Length != source.vertexCount) throw new ArgumentException(ApmLocalization.T("본 웨이트 개수가 정점과 맞지 않습니다."));
                var offsets = new int[counts.Length + 1];
                for (int i = 0; i < counts.Length; i++) offsets[i + 1] = offsets[i] + counts[i];
                var output = new List<BoneWeight1>(); var outputCounts = new byte[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int v = vertices[i]; outputCounts[i] = counts[v];
                    for (int j = offsets[v]; j < offsets[v + 1]; j++) output.Add(weights[j]);
                }
                using (var c = new NativeArray<byte>(outputCounts, Allocator.Temp))
                using (var w = new NativeArray<BoneWeight1>(output.ToArray(), Allocator.Temp)) target.SetBoneWeights(c, w);
            }
        }

        static void CopyShapes(Mesh source, Mesh target, int[] vertices)
        {
            var dv = new Vector3[source.vertexCount]; var dn = new Vector3[source.vertexCount]; var dt = new Vector3[source.vertexCount];
            for (int s = 0; s < source.blendShapeCount; s++)
                for (int f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                {
                    source.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    target.AddBlendShapeFrame(source.GetBlendShapeName(s), source.GetBlendShapeFrameWeight(s, f), Select(dv, vertices), Select(dn, vertices), Select(dt, vertices));
                }
        }
    }
}
