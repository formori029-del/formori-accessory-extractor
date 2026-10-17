using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public sealed class ApmRecipe : ScriptableObject
    {
        public int version=2;
        public bool sessionOnly;
        public string meshGuid;
        public long meshLocalId;
        public string meshFingerprint;
        public string rendererPath;
        public string rendererType;
        public string boneRootPath;
        public string attachmentPath;
        public bool hasBoneRoot;
        public bool hasAttachment;
        public ExtractionMode mode;
        public bool automaticBones;
        public List<int> faces=new List<int>();
        public string outputName;
        public bool useCustomName;
        public string customName;
        public bool useMA;
        public bool excludeExternalColliders;
        public Vector3 positionOffset;
        public Vector3 rotationOffset;
        public Vector3 scale=Vector3.one;
        public PlacementMode placement;
        public bool singleObject=true;

        public static ApmRecipe Capture(ApmOptions options)
        {
            if(options==null || !options.source) throw new ArgumentException(ApmLocalization.T("원본 Renderer가 필요합니다."));
            var mesh=MeshSelection.GetMesh(options.source);if(!mesh) throw new ArgumentException(ApmLocalization.T("원본 Mesh가 필요합니다."));
            var result=CreateInstance<ApmRecipe>();
            try
            {
                result.sessionOnly=!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,out result.meshGuid,out result.meshLocalId);
                result.meshFingerprint=MeshSelection.Fingerprint(mesh);
                result.rendererType=options.source.GetType().FullName;
                result.hasBoneRoot=options.boneRoot;result.hasAttachment=options.attachTarget;
                // An explicit avatar root is necessary for portable transform paths.
                if((options.boneRoot || options.attachTarget) && !options.avatarRoot) result.sessionOnly=true;
                try
                {
                    result.rendererPath=options.avatarRoot ? RigAnalysis.Path(options.avatarRoot,options.source.transform):"";
                    if(options.avatarRoot)
                    {
                        if(options.boneRoot) result.boneRootPath=RigAnalysis.Path(options.avatarRoot,options.boneRoot);
                        if(options.attachTarget) result.attachmentPath=RigAnalysis.Path(options.avatarRoot,options.attachTarget);
                    }
                }
                // Duplicate sibling names make paths ambiguous: keep the prefab, store a session-only recipe.
                catch(InvalidOperationException) { result.sessionOnly=true; }
                result.useCustomName=options.useCustomName;result.customName=options.customName;
                result.mode=options.mode;result.automaticBones=options.automaticBones;result.faces=new List<int>(options.faces);result.outputName=options.name;result.useMA=options.useMA;result.excludeExternalColliders=options.excludeExternalColliders;
                result.positionOffset=options.positionOffset;result.rotationOffset=options.rotationOffset;result.scale=options.scale;result.placement=options.placement;result.singleObject=options.singleObject;return result;
            }
            catch { DestroyImmediate(result);throw; }
        }
        public bool TryRestore(Renderer source,Transform avatarRoot,out ApmOptions options,out string error)
        {
            options=null;error=null;
            if(version!=1 && version!=2) { error=ApmLocalization.T("지원하지 않는 레시피 버전입니다.");return false; }
            if(sessionOnly) { error=ApmLocalization.T("세션 전용 원본이므로 휴대용 레시피를 복원할 수 없습니다. 원본 Mesh를 에셋으로 저장하고 다시 선택하세요.");return false; }
            if(!source) { error=ApmLocalization.T("복원할 Renderer를 지정하세요.");return false; }
            var mesh=MeshSelection.GetMesh(source);
            if(!mesh || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,out string guid,out long localId) || guid!=meshGuid || localId!=meshLocalId) { error=ApmLocalization.T("레시피와 다른 원본 Mesh입니다.");return false; }
            try
            {
                if(source.GetType().FullName!=rendererType || MeshSelection.Fingerprint(mesh)!=meshFingerprint) { error=ApmLocalization.T("원본 Mesh 또는 Renderer가 변경되었습니다. 면을 다시 선택하세요.");return false; }
                if((hasBoneRoot || hasAttachment) && !avatarRoot) { error=ApmLocalization.T("아바타 루트를 지정하세요.");return false; }
                Transform bone=null,attach=null;
                if(hasBoneRoot) bone=string.IsNullOrEmpty(boneRootPath) ? avatarRoot:avatarRoot.Find(boneRootPath);
                if(hasAttachment) attach=string.IsNullOrEmpty(attachmentPath) ? avatarRoot:avatarRoot.Find(attachmentPath);
                if((hasBoneRoot && !bone) || (hasAttachment && !attach)) { error=ApmLocalization.T("레시피에 저장된 본 또는 부착 경로가 없습니다.");return false; }
                if(avatarRoot)
                {
                    if(RigAnalysis.Path(avatarRoot,source.transform)!=rendererPath || (bone && RigAnalysis.Path(avatarRoot,bone)!=boneRootPath) || (attach && RigAnalysis.Path(avatarRoot,attach)!=attachmentPath)) { error=ApmLocalization.T("Renderer 또는 본 경로가 레시피와 다릅니다.");return false; }
                }
                options=new ApmOptions {source=source,avatarRoot=avatarRoot,boneRoot=bone,attachTarget=attach,mode=mode,automaticBones=automaticBones,faces=new List<int>(faces),name=outputName,useCustomName=version==1 || useCustomName,customName=version==1 ? outputName : customName,useMA=useMA,excludeExternalColliders=excludeExternalColliders,positionOffset=positionOffset,rotationOffset=rotationOffset,scale=scale,placement=placement,singleObject=singleObject};return true;
            }
            catch(Exception e) { error=ApmLocalization.T("레시피 복원 실패: ")+e.Message;return false; }
        }
    }
}
