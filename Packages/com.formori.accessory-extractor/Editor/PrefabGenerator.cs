using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AccessoryPrefabMaker
{
    public sealed class GeneratedAccessory : IDisposable
    {
        public GameObject root;
        public Mesh mesh;
        public void Dispose()
        {
            if(root && !EditorUtility.IsPersistent(root)) Object.DestroyImmediate(root);
            if(mesh && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
            root=null;mesh=null;
        }
    }

    public static class PrefabGenerator
    {
        public static ValidationReport Validate(ApmOptions o)
        {
            var r=new ValidationReport();
            if(o==null || !o.source) { r.errors.Add(ApmLocalization.T("원본 Renderer를 지정하세요."));return r; }
            if(EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) r.errors.Add(ApmLocalization.T("Play/Animation 미리보기를 종료한 편집 포즈에서 생성하세요."));
            if(EditorUtility.IsPersistent(o.source)) r.errors.Add(ApmLocalization.T("씬의 Renderer를 지정하세요."));
            var mesh=MeshSelection.GetMesh(o.source);
            if(!mesh) { r.errors.Add(ApmLocalization.T("원본 Mesh가 없습니다."));return r; }
            if(!mesh.isReadable) { r.errors.Add(ApmLocalization.T("원본 Mesh의 Read/Write를 켜세요."));return r; }
            if(o.faces==null || o.faces.Count==0) r.errors.Add(ApmLocalization.T("추출할 삼각형을 선택하세요."));
            try
            {
                RigAnalysis.ResolveAutomatic(o);
                var faces=MeshSelection.Faces(mesh);
                if(o.faces!=null && o.faces.Any(f=>f<0 || f>=faces.Length)) r.errors.Add(ApmLocalization.T("선택 인덱스가 원본 Mesh와 일치하지 않습니다."));
                if(!RigAnalysis.Uniform(o.scale)) r.errors.Add(ApmLocalization.T("오프셋 스케일은 양수 균일 스케일이어야 합니다."));
                if(!Finite(o.positionOffset) || !Finite(o.rotationOffset)) r.errors.Add(ApmLocalization.T("오프셋 값이 유효하지 않습니다."));
                RigAnalysis.ValidateScale(o.source.transform,r); if(o.attachTarget) RigAnalysis.ValidateScale(o.attachTarget,r);
                foreach(var material in o.source.sharedMaterials) if(material && !EditorUtility.IsPersistent(material)) r.errors.Add(ApmLocalization.T("씬 전용 Material은 먼저 에셋으로 저장하세요: ")+material.name);
                if(o.source.HasPropertyBlock()) r.errors.Add(ApmLocalization.T("MaterialPropertyBlock을 사용하는 Renderer는 먼저 머티리얼 에셋으로 정리하세요."));
                if(o.mode==ExtractionMode.Skinned && o.faces!=null) RigAnalysis.ValidateRig(o,mesh,r);
                var skinnedSource=o.source as SkinnedMeshRenderer;
                if(o.mode==ExtractionMode.Skinned && skinnedSource && skinnedSource.rootBone) RigAnalysis.ValidateScale(skinnedSource.rootBone,r);
                if(o.mode==ExtractionMode.Rigid) r.warnings.Add(ApmLocalization.T("고정 모드: 현재 편집 포즈를 베이크하며 본·PhysBone·동적 쉐이프키는 유지하지 않습니다."));
                if(o.mode==ExtractionMode.Rigid && RigAnalysis.MovesRigid(o)) r.warnings.Add(ApmLocalization.T("고정 모드는 이 메쉬를 움직이는 Constraint·PhysBone을 복사하지 않습니다. 결과는 현재 위치에 고정됩니다."));
                if(o.bodyRig) r.details.Add(ApmLocalization.T("여러 몸 관절: 복사한 아마추어를 MA Merge Armature로 아바타 몸에 합칩니다 · ")+o.boneRoot.name);
                if(o.source.probeAnchor) { RigAnalysis.ValidateScale(o.source.probeAnchor,r);r.warnings.Add(ApmLocalization.T("Probe Anchor는 위치를 보존한 내부 Transform으로 연결합니다.")); }
                if(o.placement==PlacementMode.Ground && (o.useMA || o.bodyRig)) r.warnings.Add(ApmLocalization.T("MA 부착·몸 관절 메쉬는 아바타에 붙어야 동작하므로 원본 따라가기로 저장합니다."));
                if(o.useMA && !o.bodyRig)
                {
                    var type=RigAnalysis.FindType(RigAnalysis.BoneProxy);
                    if(type==null || type.GetField("boneReference")==null || type.GetField("subPath")==null || type.GetField("attachmentMode")==null || type.GetField("matchScale")==null) r.errors.Add(ApmLocalization.T("지원하는 Modular Avatar Bone Proxy가 설치되어 있지 않습니다."));
                    if(!o.avatarRoot || !o.attachTarget) r.errors.Add(ApmLocalization.T("MA 출력에는 아바타 루트와 부착 대상이 필요합니다."));
                    else RigAnalysis.Path(o.avatarRoot,o.attachTarget);
                }
                if(!AssetDatabase.Contains(mesh)) r.warnings.Add(ApmLocalization.T("원본 Mesh는 세션 전용입니다. 저장 레시피의 휴대용 복원은 사용할 수 없습니다."));
                r.details.Add(ApmLocalization.T("선택 삼각형: ")+(o.faces==null ? 0:o.faces.Distinct().Count()));
            }
            catch(Exception e) { r.errors.Add(ApmLocalization.T("검사 실패: ")+e.Message); }
            r.errors=r.errors.Distinct().ToList();r.warnings=r.warnings.Distinct().ToList();return r;
        }
        static bool Finite(Vector3 v) { return RigAnalysis.Finite(v.x) && RigAnalysis.Finite(v.y) && RigAnalysis.Finite(v.z); }
        static void RequireValid(ValidationReport report) { if(!report.IsValid) throw new InvalidOperationException(string.Join("\n",report.errors)); }
        public static GeneratedAccessory Preview(ApmOptions o)
        {
            RequireValid(Validate(o));
            var result=new GeneratedAccessory(); Mesh baked=null; MeshSlice slice=null;
            try
            {
                var sourceMesh=MeshSelection.GetMesh(o.source);
                if(o.mode==ExtractionMode.Rigid && o.source is SkinnedMeshRenderer)
                {
                    baked=new Mesh(); ((SkinnedMeshRenderer)o.source).BakeMesh(baked,false);sourceMesh=baked;
                }
                slice=MeshExtraction.Extract(sourceMesh,o.faces,o.mode==ExtractionMode.Skinned);result.mesh=slice.mesh;result.mesh.name=SafeName(o.name)+" Mesh";
                result.root=new GameObject(SafeName(o.name));result.root.hideFlags=HideFlags.HideAndDontSave;
                // Components must not evaluate until every source/target points into the copy.
                result.root.SetActive(false);
                var anchor=o.attachTarget ? o.attachTarget:o.source.transform;
                CopyWorld(anchor,result.root.transform);
                var content=new GameObject("Content").transform;content.SetParent(result.root.transform,false);
                var map=new Dictionary<Object,Object>();
                if(o.attachTarget) { map[o.attachTarget]=content; map[o.attachTarget.gameObject]=content.gameObject; }
                if(o.mode==ExtractionMode.Skinned) CloneTransforms(o.boneRoot,content,map,RigAnalysis.CopyTransforms(o));
                Transform holder;
                if(map.TryGetValue(o.source.transform,out var existing)) holder=(Transform)existing;
                else {
                    holder=new GameObject("Mesh").transform;
                    var parent=o.source.transform.parent;
                    holder.SetParent(parent && map.TryGetValue(parent,out var mappedParent) ? (Transform)mappedParent:content,false);
                    CopyWorld(o.source.transform,holder);map[o.source.transform]=holder;map[o.source.gameObject]=holder.gameObject;
                }
                Renderer renderer;
                if(o.mode==ExtractionMode.Skinned)
                {
                    var source=(SkinnedMeshRenderer)o.source;var output=holder.gameObject.AddComponent<SkinnedMeshRenderer>();renderer=output;
                    output.sharedMesh=result.mesh;
                    output.bones=RemapWeights(result.mesh,source,map);
                    if(source.rootBone)
                    {
                        if(!map.TryGetValue(source.rootBone,out var rootBone)) { var internalRoot=new GameObject("Skin Root").transform;internalRoot.SetParent(content,false);CopyWorld(source.rootBone,internalRoot);map[source.rootBone]=internalRoot;rootBone=internalRoot; }
                        output.rootBone=(Transform)rootBone;
                    }
                    else output.rootBone=null;
                    output.quality=source.quality;output.updateWhenOffscreen=source.updateWhenOffscreen;output.skinnedMotionVectors=source.skinnedMotionVectors;output.localBounds=source.localBounds;
                    for(int i=0;i<result.mesh.blendShapeCount;i++) output.SetBlendShapeWeight(i,source.GetBlendShapeWeight(i));
                }
                else { holder.gameObject.AddComponent<MeshFilter>().sharedMesh=result.mesh;renderer=holder.gameObject.AddComponent<MeshRenderer>(); }
                map[o.source]=renderer;
                renderer.sharedMaterials=SliceMaterials(o.source.sharedMaterials,sourceMesh.subMeshCount,slice.sourceSubmeshes);
                renderer.shadowCastingMode=o.source.shadowCastingMode;renderer.receiveShadows=o.source.receiveShadows;renderer.lightProbeUsage=o.source.lightProbeUsage;renderer.reflectionProbeUsage=o.source.reflectionProbeUsage;renderer.allowOcclusionWhenDynamic=o.source.allowOcclusionWhenDynamic;renderer.renderingLayerMask=o.source.renderingLayerMask;
                renderer.sortingLayerID=o.source.sortingLayerID;renderer.sortingOrder=o.source.sortingOrder;renderer.motionVectorGenerationMode=o.source.motionVectorGenerationMode;renderer.enabled=o.source.enabled;
                holder.gameObject.layer=o.source.gameObject.layer;
                if(o.source.probeAnchor)
                {
                    if(!map.TryGetValue(o.source.probeAnchor,out var probe)) { var p=new GameObject("Probe Anchor").transform;p.SetParent(content,false);CopyWorld(o.source.probeAnchor,p);map[o.source.probeAnchor]=p;probe=p; }
                    renderer.probeAnchor=(Transform)probe;
                }
                if(o.mode==ExtractionMode.Skinned) CopyComponents(o,map);
                if(o.bodyRig) AddMergeArmature(o,(Transform)map[o.boneRoot]);
                // A merged armature must keep the avatar frame; offsets would detach it from the body bones.
                else {content.localPosition=o.positionOffset;content.localRotation=Quaternion.Euler(o.rotationOffset);content.localScale=o.scale;}
                if(o.useMA && !o.bodyRig) AddBoneProxy(o,result.root);
                AssertInternalReferences(result.root);
                result.root.SetActive(true);
                slice.mesh=null;return result;
            }
            catch { result.Dispose();throw; }
            finally { if(baked) Object.DestroyImmediate(baked);if(slice!=null) slice.Dispose(); }
        }
        // Materials beyond the sub-mesh count are extra passes over the last sub-mesh (outline, fur...).
        public static Material[] SliceMaterials(Material[] materials,int sourceSubmeshes,int[] used)
        {
            var result=used.Select(i=>i<materials.Length ? materials[i]:null).ToList();
            if(materials.Length>sourceSubmeshes && used.Length>0 && used[used.Length-1]==sourceSubmeshes-1)
                for(int i=sourceSubmeshes;i<materials.Length;i++) result.Add(materials[i]);
            return result.ToArray();
        }
        // Merge Armature / Bone Proxy results only work attached to the avatar, so they always keep their source frame.
        public static bool CanGround(GameObject root)
        {
            return !root.GetComponentsInChildren<Component>(true).Any(c=>c && (c.GetType().FullName==RigAnalysis.MergeArmature || c.GetType().FullName==RigAnalysis.BoneProxy));
        }
        // Re-express the result in the avatar root's frame (upright as it looks on the avatar, avatar scale removed),
        // then move it so the geometry is centred on X/Z and its lowest point is at Y = 0. The prefab root ends at identity.
        public static void PlaceOnGround(GameObject root,Transform frame)
        {
            var t=root.transform;
            if(frame)
            {
                float frameScale=Mathf.Max(frame.lossyScale.x,1e-6f);
                var position=frame.InverseTransformPoint(t.position);var rotation=Quaternion.Inverse(frame.rotation)*t.rotation;float scale=t.lossyScale.x/frameScale;
                t.SetPositionAndRotation(position,rotation);t.localScale=Vector3.one*scale;
            }
            var renderers=root.GetComponentsInChildren<Renderer>(true);
            if(renderers.Length>0)
            {
                var bounds=MeshSelection.GeometryBounds(renderers[0]);
                foreach(var r in renderers) bounds.Encapsulate(MeshSelection.GeometryBounds(r));
                t.position+=new Vector3(-bounds.center.x,-bounds.min.y,-bounds.center.z);
            }
            // Bake the root pose into its children so the saved prefab root is at the origin.
            var children=t.Cast<Transform>().ToArray();
            foreach(var child in children) child.SetParent(null,true);
            t.SetPositionAndRotation(Vector3.zero,Quaternion.identity);t.localScale=Vector3.one;
            foreach(var child in children) child.SetParent(t,true);
        }
        static void AddMergeArmature(ApmOptions o,Transform copy)
        {
            var type=RigAnalysis.FindType(RigAnalysis.MergeArmature);
            var original=o.boneRoot.GetComponents<Component>().FirstOrDefault(c=>c && c.GetType()==type);
            var merge=copy.GetComponent(type);if(!merge) merge=copy.gameObject.AddComponent(type);
            if(original) EditorUtility.CopySerialized(original,merge);
            var so=new SerializedObject(merge);
            var path=so.FindProperty("mergeTarget.referencePath");var target=so.FindProperty("mergeTarget.targetObject");
            // The avatar's own Armature is merged into itself; an outfit armature keeps its original target path.
            if(!original && path!=null) path.stringValue=RigAnalysis.Path(o.avatarRoot,o.boneRoot);
            else if(original && path!=null && string.IsNullOrEmpty(path.stringValue) && target!=null && target.objectReferenceValue)
                path.stringValue=RigAnalysis.Path(o.avatarRoot,RigAnalysis.TransformOf(target.objectReferenceValue));
            // Scene references cannot live in a prefab; MA resolves the path inside whatever avatar holds it.
            if(target!=null) target.objectReferenceValue=null;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        static void CopyWorld(Transform source,Transform target)
        {
            target.SetPositionAndRotation(source.position,source.rotation);
            float parentScale=target.parent ? target.parent.lossyScale.x:1;
            target.localScale=Vector3.one*(source.lossyScale.x/parentScale);
        }
        static Transform[] RemapWeights(Mesh mesh,SkinnedMeshRenderer source,Dictionary<Object,Object> map)
        {
            byte[] counts;BoneWeight1[] weights;
            using(var sourceCounts=mesh.GetBonesPerVertex()) using(var sourceWeights=mesh.GetAllBoneWeights()) { counts=sourceCounts.ToArray();weights=sourceWeights.ToArray(); }
            var palette=weights.Where(w=>w.weight>0).Select(w=>w.boneIndex).Distinct().OrderBy(i=>i).ToArray();
            var remap=new Dictionary<int,int>();for(int i=0;i<palette.Length;i++) remap[palette[i]]=i;
            var bones=new Transform[palette.Length];var poses=mesh.bindposes;
            for(int i=0;i<palette.Length;i++)
            {
                int original=palette[i];if(original<0 || original>=source.bones.Length || original>=poses.Length || !source.bones[original] || !map.TryGetValue(source.bones[original],out var mapped)) throw new InvalidOperationException(ApmLocalization.T("필수 본을 내부 본으로 연결할 수 없습니다: ")+original);
                bones[i]=(Transform)mapped;
            }
            for(int i=0;i<weights.Length;i++) { var weight=weights[i];weight.boneIndex=weight.weight>0 ? remap[weight.boneIndex]:0;weights[i]=weight; }
            mesh.bindposes=palette.Select(i=>poses[i]).ToArray();
            using(var newCounts=new NativeArray<byte>(counts,Allocator.Temp)) using(var newWeights=new NativeArray<BoneWeight1>(weights,Allocator.Temp)) mesh.SetBoneWeights(newCounts,newWeights);
            return bones;
        }
        static void CloneTransforms(Transform source,Transform parent,Dictionary<Object,Object> map,HashSet<Transform> keep)
        {
            Transform copy;
            if(map.TryGetValue(source,out var existing)) copy=(Transform)existing;
            else {
                copy=new GameObject(source.name).transform;copy.SetParent(parent,false);CopyWorld(source,copy);
                copy.gameObject.layer=source.gameObject.layer;copy.gameObject.SetActive(source.gameObject.activeSelf);map[source]=copy;map[source.gameObject]=copy.gameObject;
            }
            foreach(Transform child in source) if(keep.Contains(child)) CloneTransforms(child,copy,map,keep);
        }
        static void CopyComponents(ApmOptions o,Dictionary<Object,Object> map)
        {
            var sources=RigAnalysis.CopyComponents(o,RigAnalysis.CopyTransforms(o));
            foreach(var source in sources)
            {
                if(!map.ContainsKey(source.transform))
                {
                    var host=new GameObject(source.name+" Settings").transform;host.SetParent((Transform)map[o.attachTarget],false);CopyWorld(source.transform,host);
                    host.gameObject.layer=source.gameObject.layer;
                    bool active=true;var boundary=source.transform;
                    while(boundary && !RigAnalysis.Within(o.attachTarget,boundary)) boundary=boundary.parent;
                    for(var t=source.transform;t && t!=boundary;t=t.parent) active &= t.gameObject.activeSelf;
                    host.gameObject.SetActive(active);
                    map[source.transform]=host;map[source.gameObject]=host.gameObject;
                }
                map[source]=((Transform)map[source.transform]).gameObject.AddComponent(source.GetType());
            }
            foreach(var source in sources)
            {
                var copy=(Component)map[source];EditorUtility.CopySerialized(source,copy);var serialized=new SerializedObject(copy);var p=serialized.GetIterator();
                while(p.Next(true))
                {
                    if(p.propertyType!=SerializedPropertyType.ObjectReference || RigAnalysis.IsOwnershipProperty(p.propertyPath)) continue;
                    var value=p.objectReferenceValue;if(!value || EditorUtility.IsPersistent(value)) continue;
                    if(map.TryGetValue(value,out var replacement)) p.objectReferenceValue=replacement;
                    else if(RigAnalysis.IsExcludedCollider(o,source,p.propertyPath,value)) p.objectReferenceValue=null;
                    else throw new InvalidOperationException(ApmLocalization.T("복제할 수 없는 외부 참조: ")+source.name+"."+p.propertyPath);
                }
                if(o.excludeExternalColliders && source.GetType().FullName==RigAnalysis.PhysBone)
                {
                    var colliders=serialized.FindProperty("colliders");
                    if(colliders!=null) for(int i=colliders.arraySize-1;i>=0;i--) if(colliders.GetArrayElementAtIndex(i).objectReferenceValue==null) colliders.DeleteArrayElementAtIndex(i);
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        static void AddBoneProxy(ApmOptions o,GameObject root)
        {
            var type=RigAnalysis.FindType(RigAnalysis.BoneProxy);var proxy=root.AddComponent(type);
            HumanBodyBones bone=HumanBodyBones.LastBone;Transform basis=o.avatarRoot;
            var animator=o.avatarRoot.GetComponent<Animator>();
            if(animator && animator.isHuman)
            {
                // An outfit Chest merged by MA Merge Armature becomes the avatar Chest at build time.
                var merged=RigAnalysis.MergedBoneMap(o.avatarRoot);
                if(merged.TryGetValue(o.attachTarget,out var body))
                    foreach(HumanBodyBones candidate in Enum.GetValues(typeof(HumanBodyBones))) if(candidate!=HumanBodyBones.LastBone && animator.GetBoneTransform(candidate)==body) { bone=candidate;basis=o.attachTarget;break; }
                if(bone==HumanBodyBones.LastBone)
                for(var t=o.attachTarget;t && t!=o.avatarRoot;t=t.parent)
                {
                    foreach(HumanBodyBones candidate in Enum.GetValues(typeof(HumanBodyBones))) if(candidate!=HumanBodyBones.LastBone && animator.GetBoneTransform(candidate)==t) { bone=candidate;basis=t;break; }
                    if(bone!=HumanBodyBones.LastBone) break;
                }
            }
            type.GetField("boneReference").SetValue(proxy,bone);
            type.GetField("subPath").SetValue(proxy,o.attachTarget==o.avatarRoot ? "$$AVATAR":RigAnalysis.Path(basis,o.attachTarget));
            var mode=type.GetField("attachmentMode");mode.SetValue(proxy,Enum.Parse(mode.FieldType,"AsChildAtRoot"));
            type.GetField("matchScale").SetValue(proxy,true);
        }
        public static void AssertInternalReferences(GameObject root)
        {
            foreach(var component in root.GetComponentsInChildren<Component>(true)) foreach(var reference in RigAnalysis.References(component))
            {
                var value=reference.Value;if(!value || EditorUtility.IsPersistent(value) || value is Mesh) continue;
                var transform=RigAnalysis.TransformOf(value);
                if(!RigAnalysis.Within(transform,root.transform)) throw new InvalidOperationException(ApmLocalization.T("프리팹 외부 참조: ")+component.name+"."+reference.Key);
            }
        }
        public static string Save(ApmOptions o,string assetsFolder)
        {
            var report=Validate(o);RequireValid(report);
            if(string.IsNullOrWhiteSpace(assetsFolder)) throw new ArgumentException(ApmLocalization.T("Assets 저장 폴더를 지정하세요."));
            assetsFolder=assetsFolder.Replace('\\','/').TrimEnd('/');
            if((assetsFolder!="Assets" && !assetsFolder.StartsWith("Assets/",StringComparison.Ordinal)) || assetsFolder.Split('/').Any(p=>p==".." || p=="." || p.Length==0) || !AssetDatabase.IsValidFolder(assetsFolder)) throw new ArgumentException(ApmLocalization.T("존재하는 프로젝트 Assets 폴더를 지정하세요."));
            var name=SafeName(o.name);string output=null;bool createdFolder=false;ApmRecipe recipe=null;
            using(var generated=Preview(o))
            {
                try
                {
                    output=AssetDatabase.GenerateUniqueAssetPath(assetsFolder+"/"+name);
                    var guid=AssetDatabase.CreateFolder(assetsFolder,Path.GetFileName(output));if(string.IsNullOrEmpty(guid)) throw new IOException(ApmLocalization.T("출력 폴더 생성 실패"));
                    output=AssetDatabase.GUIDToAssetPath(guid);createdFolder=true;
                    AssetDatabase.CreateAsset(generated.mesh,output+"/"+name+".mesh.asset");
                    recipe=ApmRecipe.Capture(o);AssetDatabase.CreateAsset(recipe,output+"/"+name+".recipe.asset");
                    generated.root.hideFlags=HideFlags.None;
                    if(o.placement==PlacementMode.Ground && CanGround(generated.root)) PlaceOnGround(generated.root,o.avatarRoot);
                    else {generated.root.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);generated.root.transform.localScale=Vector3.one;}
                    AssertInternalReferences(generated.root);
                    var prefabPath=output+"/"+name+".prefab";
                    var prefab=PrefabUtility.SaveAsPrefabAsset(generated.root,prefabPath,out bool success);if(!success || !prefab) throw new IOException(ApmLocalization.T("프리팹 저장 실패"));
                    File.WriteAllText(output+"/"+name+".validation.json",JsonUtility.ToJson(report,true));AssetDatabase.ImportAsset(output+"/"+name+".validation.json");
                    AssetDatabase.SaveAssetIfDirty(generated.mesh);AssetDatabase.SaveAssetIfDirty(recipe);AssetDatabase.SaveAssetIfDirty(prefab);AssertInternalReferences(prefab);return prefabPath;
                }
                catch { if(createdFolder && AssetDatabase.IsValidFolder(output)) AssetDatabase.DeleteAsset(output);throw; }
                finally { if(recipe && !EditorUtility.IsPersistent(recipe)) Object.DestroyImmediate(recipe); }
            }
        }
        static string SafeName(string value)
        {
            if(string.IsNullOrWhiteSpace(value)) return "Accessory";
            var invalid=Path.GetInvalidFileNameChars();var clean=new string(value.Trim().Select(c=>invalid.Contains(c) || c=='/' || c=='\\' ? '_':c).ToArray()).Trim('.');
            return string.IsNullOrWhiteSpace(clean) ? "Accessory":clean;
        }
    }
}
