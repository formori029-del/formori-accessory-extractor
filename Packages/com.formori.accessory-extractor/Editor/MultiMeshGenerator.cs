using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace AccessoryPrefabMaker
{
    public sealed class MultiMeshResult : IDisposable
    {
        public GameObject root;
        public readonly List<Mesh> meshes=new List<Mesh>();
        public void Dispose()
        {
            if(root && !EditorUtility.IsPersistent(root)) Object.DestroyImmediate(root);
            foreach(var mesh in meshes) if(mesh && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
            root=null;meshes.Clear();
        }
    }

    public static class MultiMeshGenerator
    {
        static string T(string ko,string en,string ja) {return ApmLocalization.Language==0?ko:ApmLocalization.Language==1?en:ja;}
        public static MultiMeshResult Build(Renderer[] sources,string name,bool merge)
        {
            if(sources==null || sources.Length<2 || sources.Any(r=>!r) || sources.Distinct().Count()!=sources.Length)
                throw new ArgumentException(T("서로 다른 원본 메쉬를 2개 이상 지정하세요.","Choose at least two distinct source meshes.","異なる元メッシュを2つ以上指定してください。"));
            var result=new MultiMeshResult();
            try
            {
                result.root=new GameObject(SafeName(name));result.root.hideFlags=HideFlags.HideAndDontSave;
                result.root.SetActive(false);
                result.root.transform.SetPositionAndRotation(sources[0].transform.position,sources[0].transform.rotation);
                // Each source keeps its own rig/attachment frame, even when bone names match.
                for(int i=0;i<sources.Length;i++)
                {
                    var source=sources[i];
                    // Same automatic setup as simple mode / "extract separately": body colliders outside the copy are dropped, not errors.
                    var options=new ApmOptions {source=source,automaticBones=true,excludeExternalColliders=true,mode=RigAnalysis.DefaultMode(source),name=source.name};
                    foreach(var c in source.GetComponentsInParent<Component>(true))
                        if(c && c.GetType().FullName=="VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") {options.avatarRoot=c.transform;break;}
                    var mesh=MeshSelection.GetMesh(source);
                    if(!mesh) throw new ArgumentException(T("원본 Mesh가 없습니다: ","Missing source mesh: ","元 Mesh がありません: ")+source.name);
                    options.faces=Enumerable.Range(0,MeshSelection.Faces(mesh).Length).ToList();
                    if(options.mode==ExtractionMode.Rigid && RigAnalysis.MovesRigid(options))
                        throw new InvalidOperationException(T("본 없는 메쉬의 물리/Constraint 복사는 지원하지 않습니다: ","Physics/constraints on rigid meshes are not supported: ","固定メッシュの物理・Constraint コピーは非対応です: ")+source.name);
                    using(var part=PrefabGenerator.Preview(options))
                    {
                        var copiedRenderer=part.root.GetComponentInChildren<Renderer>(true);
                        copiedRenderer.sortingLayerID=source.sortingLayerID;copiedRenderer.sortingOrder=source.sortingOrder;
                        copiedRenderer.gameObject.layer=source.gameObject.layer;
                        copiedRenderer.motionVectorGenerationMode=source.motionVectorGenerationMode;
                        part.root.SetActive(false);
                        part.root.transform.SetParent(result.root.transform,true);
                        part.root.name=(i+1)+"_"+SafeName(source.name);
                        part.root.hideFlags=HideFlags.None;
                        result.meshes.Add(part.mesh);
                        part.root.SetActive(true);part.root=null;part.mesh=null;
                    }
                }
                if(merge) MergeRenderers(result);
                PrefabGenerator.AssertInternalReferences(result.root);
                result.root.SetActive(true);
                return result;
            }
            catch {result.Dispose();throw;}
        }

        static void MergeRenderers(MultiMeshResult result)
        {
            var renderers=result.root.GetComponentsInChildren<Renderer>(true);
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var tangents=new List<Vector4>();var colors=new List<Color>();
            var uv=Enumerable.Range(0,8).Select(_=>new List<Vector4>()).ToArray();var uvDimensions=new int[8];
            var bones=new List<Transform>();var bindposes=new List<Matrix4x4>();var counts=new List<byte>();var weights=new List<BoneWeight1>();
            var triangles=new List<int[]>();var materials=new List<Material>();var transforms=new List<Matrix4x4>();var offsets=new List<int>();
            bool hasNormals=false,hasTangents=false,hasColors=false;
            var first=renderers[0];
            foreach(var r in renderers)
            {
                if(r.allowOcclusionWhenDynamic!=first.allowOcclusionWhenDynamic || r.motionVectorGenerationMode!=first.motionVectorGenerationMode || r.gameObject.layer!=first.gameObject.layer)
                    throw new InvalidOperationException(T("Renderer 설정이 서로 다릅니다. '프리팹으로 묶기'를 사용하세요.","Renderer settings differ. Use 'Group in one prefab'.","Renderer 設定が異なります。「1つのプレハブにまとめる」を使用してください。"));
                if(r.shadowCastingMode!=first.shadowCastingMode || r.receiveShadows!=first.receiveShadows || r.lightProbeUsage!=first.lightProbeUsage || r.reflectionProbeUsage!=first.reflectionProbeUsage || r.renderingLayerMask!=first.renderingLayerMask || r.sortingLayerID!=first.sortingLayerID || r.sortingOrder!=first.sortingOrder || (r.probeAnchor && first.probeAnchor && Vector3.Distance(r.probeAnchor.position,first.probeAnchor.position)>.00001f) || (bool)r.probeAnchor!=(bool)first.probeAnchor)
                    throw new InvalidOperationException(T("Renderer 설정이 서로 다릅니다. '프리팹으로 묶기'를 사용하세요.","Renderer settings differ. Use 'Group in one prefab'.","Renderer 設定が異なります。「1つのプレハブにまとめる」を使用してください。"));
                var mesh=MeshSelection.GetMesh(r);int offset=vertices.Count;offsets.Add(offset);
                var matrix=result.root.transform.worldToLocalMatrix*r.transform.localToWorldMatrix;transforms.Add(matrix);
                var normalMatrix=matrix.inverse.transpose;
                vertices.AddRange(mesh.vertices.Select(matrix.MultiplyPoint3x4));
                bool n=mesh.HasVertexAttribute(VertexAttribute.Normal),t=mesh.HasVertexAttribute(VertexAttribute.Tangent),c=mesh.HasVertexAttribute(VertexAttribute.Color);
                hasNormals|=n;hasTangents|=t;hasColors|=c;
                // A part without normals/tangents gets computed ones, never zero vectors that render black.
                Vector3[] partNormals=n?mesh.normals:null;Vector4[] partTangents=t?mesh.tangents:null;
                if(!n || !t)
                {
                    var temp=Object.Instantiate(mesh);
                    try
                    {
                        if(!n) {temp.RecalculateNormals();partNormals=temp.normals;}
                        if(!t && temp.HasVertexAttribute(VertexAttribute.TexCoord0)) {temp.normals=partNormals;temp.RecalculateTangents();partTangents=temp.tangents;}
                    }
                    finally {Object.DestroyImmediate(temp);}
                }
                normals.AddRange(partNormals.Select(v=>normalMatrix.MultiplyVector(v).normalized));
                tangents.AddRange(partTangents!=null?partTangents.Select(v=>{var d=matrix.MultiplyVector(new Vector3(v.x,v.y,v.z)).normalized;return new Vector4(d.x,d.y,d.z,v.w);}):Enumerable.Repeat(new Vector4(1,0,0,1),mesh.vertexCount));
                colors.AddRange(c?mesh.colors:Enumerable.Repeat(Color.white,mesh.vertexCount));
                for(int channel=0;channel<8;channel++)
                {
                    var attribute=(VertexAttribute)((int)VertexAttribute.TexCoord0+channel);var values=new List<Vector4>();mesh.GetUVs(channel,values);
                    if(mesh.HasVertexAttribute(attribute)) uvDimensions[channel]=Math.Max(uvDimensions[channel],mesh.GetVertexAttributeDimension(attribute));
                    uv[channel].AddRange(values.Count==mesh.vertexCount?values:Enumerable.Repeat(Vector4.zero,mesh.vertexCount));
                }
                for(int sub=0;sub<mesh.subMeshCount;sub++) {triangles.Add(mesh.GetTriangles(sub).Select(v=>v+offset).ToArray());materials.Add(sub<r.sharedMaterials.Length?r.sharedMaterials[sub]:null);}
                // Extra material passes draw the last sub-mesh again; in a merged mesh they need their own copy of it.
                for(int extra=mesh.subMeshCount;extra<r.sharedMaterials.Length && mesh.subMeshCount>0;extra++) {triangles.Add(mesh.GetTriangles(mesh.subMeshCount-1).Select(v=>v+offset).ToArray());materials.Add(r.sharedMaterials[extra]);}
                int boneOffset=bones.Count;
                if(r is SkinnedMeshRenderer skin)
                {
                    bones.AddRange(skin.bones);bindposes.AddRange(mesh.bindposes.Select(p=>p*matrix.inverse));
                    using(var bc=mesh.GetBonesPerVertex()) using(var bw=mesh.GetAllBoneWeights())
                    {counts.AddRange(bc.ToArray());foreach(var weight in bw) weights.Add(new BoneWeight1 {boneIndex=weight.boneIndex+boneOffset,weight=weight.weight});}
                }
                else
                {
                    bones.Add(r.transform);bindposes.Add(matrix.inverse);
                    for(int v=0;v<mesh.vertexCount;v++) {counts.Add(1);weights.Add(new BoneWeight1 {boneIndex=boneOffset,weight=1});}
                }
            }
            var combined=new Mesh {name=result.root.name+" Mesh",indexFormat=vertices.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16};result.meshes.Add(combined);
            combined.SetVertices(vertices);if(hasNormals)combined.SetNormals(normals);if(hasTangents)combined.SetTangents(tangents);if(hasColors)combined.SetColors(colors);
            for(int ch=0;ch<8;ch++)
            {
                // 1D UVs are stored as 2D (y=0); the first component is unchanged.
                if(uvDimensions[ch]==1 || uvDimensions[ch]==2) combined.SetUVs(ch,uv[ch].Select(v=>new Vector2(v.x,v.y)).ToList());
                else if(uvDimensions[ch]==3) combined.SetUVs(ch,uv[ch].Select(v=>new Vector3(v.x,v.y,v.z)).ToList());
                else if(uvDimensions[ch]==4) combined.SetUVs(ch,uv[ch]);
            }
            combined.subMeshCount=triangles.Count;for(int i=0;i<triangles.Count;i++) combined.SetTriangles(triangles[i],i,false);
            combined.bindposes=bindposes.ToArray();
            using(var bc=new NativeArray<byte>(counts.ToArray(),Allocator.Temp)) using(var bw=new NativeArray<BoneWeight1>(weights.ToArray(),Allocator.Temp)) combined.SetBoneWeights(bc,bw);
            var shapeWeights=new List<float>();
            for(int i=0;i<renderers.Length;i++)
            {
                if(!(renderers[i] is SkinnedMeshRenderer skin)) continue;
                var source=skin.sharedMesh;var matrix=transforms[i];var normalMatrix=matrix.inverse.transpose;
                for(int shape=0;shape<source.blendShapeCount;shape++)
                {
                    string shapeName=(i+1)+"_"+source.GetBlendShapeName(shape);
                    for(int frame=0;frame<source.GetBlendShapeFrameCount(shape);frame++)
                    {
                        var dv=new Vector3[source.vertexCount];var dn=new Vector3[source.vertexCount];var dt=new Vector3[source.vertexCount];source.GetBlendShapeFrameVertices(shape,frame,dv,dn,dt);
                        var allV=new Vector3[vertices.Count];var allN=new Vector3[vertices.Count];var allT=new Vector3[vertices.Count];
                        float scale=matrix.lossyScale.x;
                        for(int v=0;v<source.vertexCount;v++) {allV[offsets[i]+v]=matrix.MultiplyVector(dv[v]);allN[offsets[i]+v]=normalMatrix.MultiplyVector(dn[v])*scale;allT[offsets[i]+v]=matrix.MultiplyVector(dt[v])/scale;}
                        combined.AddBlendShapeFrame(shapeName,source.GetBlendShapeFrameWeight(shape,frame),allV,allN,allT);
                    }
                    shapeWeights.Add(skin.GetBlendShapeWeight(shape));
                }
            }
            combined.RecalculateBounds();var bounds=combined.bounds;
            for(int i=0;i<renderers.Length;i++)
            {
                var sourceBounds=renderers[i] is SkinnedMeshRenderer sr?sr.localBounds:MeshSelection.GetMesh(renderers[i]).bounds;
                // Skinned localBounds live in the root bone's space, not the renderer's.
                var frame=renderers[i] is SkinnedMeshRenderer rooted && rooted.rootBone ? result.root.transform.worldToLocalMatrix*rooted.rootBone.localToWorldMatrix:transforms[i];
                for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                    bounds.Encapsulate(frame.MultiplyPoint3x4(sourceBounds.center+Vector3.Scale(sourceBounds.extents,new Vector3(x,y,z))));
            }
            var holder=new GameObject("Combined Mesh");holder.transform.SetParent(result.root.transform,false);
            var output=holder.AddComponent<SkinnedMeshRenderer>();output.sharedMesh=combined;output.bones=bones.ToArray();output.rootBone=result.root.transform;output.localBounds=bounds;
            output.sharedMaterials=materials.ToArray();output.shadowCastingMode=first.shadowCastingMode;output.receiveShadows=first.receiveShadows;output.lightProbeUsage=first.lightProbeUsage;output.reflectionProbeUsage=first.reflectionProbeUsage;output.renderingLayerMask=first.renderingLayerMask;
            output.probeAnchor=first.probeAnchor;output.sortingLayerID=first.sortingLayerID;output.sortingOrder=first.sortingOrder;
            output.allowOcclusionWhenDynamic=first.allowOcclusionWhenDynamic;output.motionVectorGenerationMode=first.motionVectorGenerationMode;holder.layer=first.gameObject.layer;
            var skins=renderers.OfType<SkinnedMeshRenderer>().ToArray();
            if(skins.Select(s=>s.quality).Distinct().Count()>1 || skins.Select(s=>s.skinnedMotionVectors).Distinct().Count()>1) throw new InvalidOperationException(T("스킨 품질 설정이 서로 다릅니다. '프리팹으로 묶기'를 사용하세요.","Skin settings differ. Use 'Group in one prefab'.","スキン設定が異なります。「1つのプレハブにまとめる」を使用してください。"));
            if(skins.Length>0) {output.quality=skins[0].quality;output.skinnedMotionVectors=skins[0].skinnedMotionVectors;}
            output.updateWhenOffscreen=renderers.OfType<SkinnedMeshRenderer>().Any(r=>r.updateWhenOffscreen);
            for(int i=0;i<shapeWeights.Count;i++)output.SetBlendShapeWeight(i,shapeWeights[i]);
            foreach(var renderer in renderers) {var filter=renderer.GetComponent<MeshFilter>();if(filter)Object.DestroyImmediate(filter);Object.DestroyImmediate(renderer);}
            foreach(var old in result.meshes.Where(m=>m!=combined).ToArray()) {Object.DestroyImmediate(old);result.meshes.Remove(old);}
        }
        public static string Save(Renderer[] sources,string name,bool merge,string folder,PlacementMode placement=PlacementMode.FollowSource)
        {
            folder=ApmPreferences.AssetFolder(folder);name=SafeName(name);
            using(var result=Build(sources,name,merge))
            {
                string output=null;
                try
                {
                    string path=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+name);
                    string guid=AssetDatabase.CreateFolder(folder,System.IO.Path.GetFileName(path));
                    if(string.IsNullOrEmpty(guid)) throw new System.IO.IOException(T("출력 폴더를 만들 수 없습니다.","Cannot create output folder.","出力フォルダーを作成できません。"));
                    output=AssetDatabase.GUIDToAssetPath(guid);
                    for(int i=0;i<result.meshes.Count;i++) AssetDatabase.CreateAsset(result.meshes[i],output+"/"+name+"_"+(i+1)+".mesh.asset");
                    result.root.hideFlags=HideFlags.None;
                    if(placement==PlacementMode.Ground && PrefabGenerator.CanGround(result.root)) PrefabGenerator.PlaceOnGround(result.root,BatchExtractor.AvatarRootOf(sources[0]));
                    else {result.root.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);result.root.transform.localScale=Vector3.one;}
                    string prefabPath=output+"/"+name+".prefab";
                    var prefab=PrefabUtility.SaveAsPrefabAsset(result.root,prefabPath,out bool success);
                    if(!success || !prefab) throw new System.IO.IOException(T("프리팹을 저장할 수 없습니다.","Cannot save prefab.","プレハブを保存できません。"));
                    PrefabGenerator.AssertInternalReferences(prefab);AssetDatabase.SaveAssetIfDirty(prefab);return prefabPath;
                }
                catch {if(output!=null)AssetDatabase.DeleteAsset(output);throw;}
            }
        }
        static string SafeName(string value)
        {
            if(string.IsNullOrWhiteSpace(value))return "Accessory_Set";
            var invalid=System.IO.Path.GetInvalidFileNameChars();var name=new string(value.Trim().Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim('.');
            return string.IsNullOrWhiteSpace(name)?"Accessory_Set":name;
        }
    }
}
