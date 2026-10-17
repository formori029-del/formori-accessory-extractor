using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public sealed class ApmMultiMeshWindow : EditorWindow
    {
        [SerializeField] List<Renderer> sources=new List<Renderer>();
        [SerializeField] bool merge=true,automaticName=true;
        [SerializeField] PlacementMode placement=PlacementMode.FollowSource;
        [SerializeField] string outputName="Accessory_Set";
        Vector2 scroll,orbit=new Vector2(180,10);
        string message;bool failed;
        MultiMeshResult preview;PreviewRenderUtility utility;Vector3 center;float distance;
        static string T(string ko,string en,string ja) {return ApmLocalization.Language==0?ko:ApmLocalization.Language==1?en:ja;}
        public static void Open() {var window=GetWindow<ApmMultiMeshWindow>();window.minSize=new Vector2(400,460);window.Show();}
        void OnEnable() {AssemblyReloadEvents.beforeAssemblyReload+=DisposePreview;}
        void OnDisable() {AssemblyReloadEvents.beforeAssemblyReload-=DisposePreview;DisposePreview();}
        void Changed() {DisposePreview();message=null;}
        void Run(Action action) {try {action();}catch(Exception e){DisposePreview();failed=true;message=e.Message;}}
        void OnGUI()
        {
            titleContent=new GUIContent(T("한 프리팹으로 분리","Extract into one prefab","1つのプレハブに分離"),ApmLogo.Icon);
            float previousLabelWidth=EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth=100;
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginVertical(new GUIStyle {padding=new RectOffset(14,14,12,12)});
            ApmLogo.Banner(position.width-44);GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(T("여러 오브젝트를 한 프리팹으로 분리","Extract multiple objects into one prefab","複数オブジェクトを1つのプレハブに分離"),EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();GUILayout.Label("v"+ApmVersion.Current,EditorStyles.miniLabel);int language=EditorGUILayout.Popup(ApmLocalization.Language,new[]{"한국어","English","日本語"},GUILayout.Width(90));
            if(language!=ApmLocalization.Language) {ApmLocalization.Language=language;message=null;}
            EditorGUILayout.EndHorizontal();GUILayout.Space(10);
            GUILayout.Label(T("1. 원본 목록","1. Sources","1. 元メッシュ一覧"),EditorStyles.boldLabel);
            for(int i=0;i<sources.Count;i++)
            {
                EditorGUILayout.BeginHorizontal();
                var value=(Renderer)EditorGUILayout.ObjectField((i+1).ToString(),sources[i],typeof(Renderer),true);
                if(value!=sources[i]) {sources[i]=value;Changed();}
                if(GUILayout.Button("−",GUILayout.Width(24))) {sources.RemoveAt(i);Changed();EditorGUILayout.EndHorizontal();break;}
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button(T("+ 메쉬 추가","+ Add mesh","+ メッシュ追加"))) {sources.Add(null);Changed();}
            if(GUILayout.Button(T("선택 오브젝트 추가","Add selected objects","選択オブジェクトを追加"))) Add(Selection.objects);
            EditorGUILayout.EndHorizontal();
            var drop=GUILayoutUtility.GetRect(100,44,GUILayout.ExpandWidth(true));GUI.Box(drop,T("여기에 메쉬 또는 오브젝트를 드래그","Drop meshes or GameObjects here","メッシュやオブジェクトをここにドロップ"),EditorStyles.helpBox);
            var e=Event.current;
            if(drop.Contains(e.mousePosition) && (e.type==EventType.DragUpdated || e.type==EventType.DragPerform))
            {
                DragAndDrop.visualMode=DragAndDropVisualMode.Copy;
                if(e.type==EventType.DragPerform) {DragAndDrop.AcceptDrag();Add(DragAndDrop.objectReferences);}
                e.Use();
            }
            GUILayout.Space(10);GUILayout.Label(T("2. 출력 방식","2. Output mode","2. 出力方式"),EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            merge=GUILayout.Toolbar(merge?0:1,new[]{T("메쉬 하나로 병합","Merge into one mesh","1つのメッシュに統合"),T("프리팹으로 묶기","Group in one prefab","1つのプレハブにまとめる")},GUILayout.Height(28))==0;
            if(EditorGUI.EndChangeCheck())Changed();
            EditorGUILayout.HelpBox(merge?T("하나의 SkinnedMeshRenderer로 병합합니다. 머티리얼 슬롯을 유지하고 쉐이프키 이름에 원본 번호를 붙입니다.","Combines into one SkinnedMeshRenderer. Keeps material slots; prefixes blend shapes with source numbers.","1つの SkinnedMeshRenderer に結合。マテリアルスロットを保持し、シェイプキー名に元番号を付けます。"):T("메쉬와 Renderer를 각각 유지하고 한 프리팹 안에 묶습니다.","Keeps individual meshes and Renderers under one prefab.","メッシュと Renderer を個別に保持し、1つのプレハブにまとめます。"),MessageType.None);
            EditorGUILayout.HelpBox(T("각 원본의 본 루트와 현재 배치를 보존합니다. 복사된 루트는 독립적이며, 외부 아바타의 움직임 연결이나 Animator 이식은 자동 처리하지 않습니다.","Preserves each rig root and current placement. Copied roots are independent; external avatar motion and Animator migration are not automatic.","各ボーンルートと現在の配置を保持します。コピーしたルートは独立し、外部アバターへの追従や Animator 移植は自動では行いません。"),MessageType.None);
            GUILayout.Space(10);GUILayout.Label(T("3. 저장","3. Save","3. 保存"),EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();EditorGUILayout.PrefixLabel(ApmLocalization.T("결과 위치"));
            var nextPlacement=(PlacementMode)GUILayout.Toolbar((int)placement,new[]{ApmLocalization.T("원본 따라가기"),ApmLocalization.T("바닥(원점)에 놓기")});
            EditorGUILayout.EndHorizontal();
            if(nextPlacement!=placement) {placement=nextPlacement;Changed();}
            GUILayout.Label(placement==PlacementMode.FollowSource?ApmLocalization.T("원본과 같은 자리에 붙어 원본 본을 따라 움직입니다."):ApmLocalization.T("원본 위치를 무시하고 바닥 중앙(아바타 발밑, 아바타가 없으면 월드 원점)에 반듯하게 놓습니다. 원본 본을 따라가지 않습니다."),EditorStyles.wordWrappedMiniLabel);
            // Named after the outfit (MA outfit root under the avatar), not after the first mesh.
            if(automaticName) {var first=sources.FirstOrDefault(r=>r);outputName=first?ApmSession.AutomaticName(first,BatchExtractor.AvatarRootOf(first))+"_Set":"Accessory_Set";}
            EditorGUILayout.BeginHorizontal();
            string edited=EditorGUILayout.TextField(ApmLocalization.T("프리팹 이름"),outputName);
            if(edited!=outputName) {outputName=edited;automaticName=false;Changed();}
            if(GUILayout.Button(ApmLocalization.T("자동 이름"),GUILayout.Width(85))) {automaticName=true;Changed();}
            EditorGUILayout.EndHorizontal();
            var current=AssetDatabase.LoadAssetAtPath<DefaultAsset>(ApmPreferences.OutputFolder);
            EditorGUILayout.BeginHorizontal();
            var folder=(DefaultAsset)EditorGUILayout.ObjectField(ApmLocalization.T("저장 폴더"),current,typeof(DefaultAsset),false);
            if(folder && folder!=current)Run(()=>ApmPreferences.SetOutputFolder(AssetDatabase.GetAssetPath(folder)));
            if(GUILayout.Button(ApmLocalization.T("찾기"),GUILayout.Width(54))) Run(()=>{var path=EditorUtility.OpenFolderPanel(ApmLocalization.T("저장 폴더"),System.IO.Path.GetFullPath(ApmPreferences.OutputFolder),"");if(!string.IsNullOrEmpty(path))ApmPreferences.SetOutputFolder(path);});
            EditorGUILayout.EndHorizontal();GUILayout.Label(ApmPreferences.OutputFolder,EditorStyles.wordWrappedMiniLabel);
            bool blocked=EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode();
            if(blocked) {DisposePreview();EditorGUILayout.HelpBox(ApmLocalization.T("재생 / Animation Mode를 종료하세요."),MessageType.Warning);}
            using(new EditorGUI.DisabledScope(blocked || sources.Count<2 || sources.Any(r=>!r) || string.IsNullOrWhiteSpace(outputName)))
            {
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button(ApmLocalization.T("미리보기"),GUILayout.Height(30)))Run(CreatePreview);
                if(GUILayout.Button(ApmLocalization.T("프리팹 저장"),GUILayout.Height(30)))Run(()=>{
                    DisposePreview();string path=MultiMeshGenerator.Save(sources.ToArray(),outputName,merge,ApmPreferences.OutputFolder,placement);
                    Selection.activeObject=AssetDatabase.LoadAssetAtPath<GameObject>(path);EditorGUIUtility.PingObject(Selection.activeObject);
                    failed=false;message=ApmLocalization.T("저장 완료: ")+path;
                });
                EditorGUILayout.EndHorizontal();
            }
            if(!string.IsNullOrEmpty(message))EditorGUILayout.HelpBox(message,failed?MessageType.Error:MessageType.Info);
            if(utility!=null) DrawPreview();
            EditorGUILayout.EndVertical();EditorGUILayout.EndScrollView();EditorGUIUtility.labelWidth=previousLabelWidth;
        }
        void Add(UnityEngine.Object[] values)
        {
            foreach(var value in values)
            {
                IEnumerable<Renderer> found=value is Renderer r?new[]{r}:value is GameObject go?go.GetComponentsInChildren<Renderer>(true):new Renderer[0];
                foreach(var renderer in found) if((renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && !sources.Contains(renderer))sources.Add(renderer);
            }
            Changed();
        }
        void CreatePreview()
        {
            DisposePreview();preview=MultiMeshGenerator.Build(sources.ToArray(),outputName,merge);
            utility=new PreviewRenderUtility();utility.AddSingleGO(preview.root);
            var renderers=preview.root.GetComponentsInChildren<Renderer>();var bounds=MeshSelection.GeometryBounds(renderers[0]);
            foreach(var r in renderers)bounds.Encapsulate(MeshSelection.GeometryBounds(r));center=bounds.center;distance=Mathf.Max(.02f,bounds.size.magnitude*1.5f);
            utility.camera.fieldOfView=30;utility.lights[0].intensity=1;utility.lights[0].transform.rotation=Quaternion.Euler(40,30,0);utility.lights[1].intensity=.6f;
            message=null;
        }
        void DrawPreview()
        {
            GUILayout.Label(ApmLocalization.T("드래그: 회전 / 휠: 확대·축소"),EditorStyles.miniLabel);
            var rect=GUILayoutUtility.GetRect(100,240,GUILayout.ExpandWidth(true));var e=Event.current;
            if(rect.Contains(e.mousePosition))
            {
                if(e.type==EventType.MouseDrag && e.button==0) {orbit+=e.delta*.5f;e.Use();Repaint();}
                else if(e.type==EventType.ScrollWheel) {distance=Mathf.Max(.005f,distance*Mathf.Exp(e.delta.y*.07f));e.Use();Repaint();}
            }
            if(e.type==EventType.Repaint) Run(()=>{
                utility.camera.transform.position=center+Quaternion.Euler(-orbit.y,orbit.x,0)*Vector3.forward*distance;utility.camera.transform.LookAt(center);
                utility.camera.nearClipPlane=distance/1000;utility.camera.farClipPlane=distance*100;
                utility.BeginPreview(rect,GUIStyle.none);utility.Render();utility.EndAndDrawPreview(rect);
            });
        }
        void DisposePreview() {if(utility!=null)utility.Cleanup();utility=null;if(preview!=null)preview.Dispose();preview=null;}
    }
}
