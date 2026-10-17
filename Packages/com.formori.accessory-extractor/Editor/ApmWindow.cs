using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public sealed class ApmWindow : EditorWindow
    {
        ApmSession session;
        ApmMeshEditor meshEditor;
        bool advanced, showUV, showRecipe;
        [SerializeField] bool simpleMode=true;
        // "Extract by object selection": every mesh under the Hierarchy selection becomes its own prefab.
        [SerializeField] bool objectMode;
        List<Renderer> pickedRenderers=new List<Renderer>();
        List<BatchItem> batchResults;
        // Survive script recompiles / Play mode domain reloads; restored only if the mesh is unchanged.
        [SerializeField] ApmOptions persistedOptions;
        [SerializeField] string persistedFingerprint;
        [SerializeField] bool persistedEditorOpen;
        // Advanced-mode work kept while the user looks at simple mode.
        [SerializeField] ApmOptions advancedBackup;
        [SerializeField] string advancedFingerprint;
        GeneratedAccessory preview;
        PreviewRenderUtility previewRenderer;
        Mesh comparisonMesh;
        bool compareSource;
        Vector3 previewCenter;
        float previewDistance;
        Vector2 previewOrbit = new Vector2(180, 8);
        ValidationReport report;
        ApmRecipe recipe;
        Vector2[] boundedUV;
        Vector2 scroll;
        Rect uvBounds;
        bool islands = true, instantiate;
        int slot = -1;
        int[] overlap = new int[0];
        bool overlapAdd, overlapRemove;
        Vector2 dragStart, dragEnd;
        bool dragging, dragAdd, dragRemove;
        double nextRefresh;
        string message;
        string rigSummary, rigError;
        bool rigDirty;
        double nextRigRefresh;
        readonly Vector3[] triangle = new Vector3[3];

        [MenuItem("FORMORI/Accessory Extractor")]
        public static void Open() { var window=GetWindow<ApmWindow>(ApmLocalization.T("악세사리 분리 툴")); if(window.position.width>550) window.position=new Rect(window.position.x,window.position.y,420,680); }

        void OnEnable()
        {
            DisposePreview();
            if (session) DestroyImmediate(session);
            report = null; message = null; recipe = null;
            overlap = new int[0]; overlapAdd = overlapRemove = false;
            dragging = dragAdd = dragRemove = false;
            dragStart = dragEnd = Vector2.zero;
            boundedUV = null; uvBounds = default(Rect); scroll = Vector2.zero;
            instantiate = compareSource = false;
            advanced = showRecipe = showUV = false;
            islands = true; slot = -1; nextRefresh = 0;
            previewCenter = Vector3.zero; previewDistance = 0;
            previewOrbit = new Vector2(180, 8);
            session = CreateInstance<ApmSession>();
            session.hideFlags = HideFlags.HideAndDontSave;
            session.changed = SelectionChanged;

            if (persistedOptions != null && persistedOptions.source)
            {
                var saved = persistedOptions; var fingerprint = persistedFingerprint; bool reopen = persistedEditorOpen && !simpleMode;
                Run(() => {
                    session.RestoreState(saved, fingerprint, false);
                    if (simpleMode) session.SelectWholeMesh();
                    Undo.ClearUndo(session);
                });
                if (reopen) EditorApplication.delayCall += () => { if (this && session && session.mesh) Run(OpenMeshEditor); };
            }
            persistedOptions = null; persistedEditorOpen = false;

            Undo.undoRedoPerformed += OnUndo;
            Selection.selectionChanged += PickSelection; PickSelection();
            AssemblyReloadEvents.beforeAssemblyReload += DisposePreview;
            EditorApplication.update += PollSource;
            EditorApplication.projectChanged += ProjectChanged;
            minSize = new Vector2(360, 440);
        }
        void OnDisable()
        {
            if (session && session.options.source) { persistedOptions = ApmSession.Clone(session.options); persistedFingerprint = session.FingerprintValue; persistedEditorOpen = meshEditor; }
            if (meshEditor) meshEditor.Close();
            if (uvTarget) { uvTarget.Release(); DestroyImmediate(uvTarget); uvTarget = null; }
            Undo.undoRedoPerformed -= OnUndo;
            Selection.selectionChanged -= PickSelection;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposePreview;
            EditorApplication.update -= PollSource;
            EditorApplication.projectChanged -= ProjectChanged;
            DisposePreview();
            if (session) DestroyImmediate(session);
        }
        void OnUndo()
        {
            Run(() => { session.RefreshSource(); session.RebuildSelected(); SelectionChanged(); });
        }
        void SelectionChanged()
        {
            DisposePreview(); report = null; overlap = new int[0];
            rigDirty=true;
            if (meshEditor) meshEditor.SelectionUpdated();
            Repaint();
        }
        void UpdateRigSummary()
        {
            rigDirty=false;nextRigRefresh=EditorApplication.timeSinceStartup+.2;
            rigSummary=null;rigError=null;
            if(session && session.options.automaticBones && session.options.mode==ExtractionMode.Skinned)
            {
                var o=session.options;
                o.boneRoot=null;o.attachTarget=null;
                if(o.faces.Count>0) try {
                    RigAnalysis.ResolveAutomatic(o);
                    rigSummary=ApmLocalization.T("웨이트 본 ")+RigAnalysis.RequiredBones(o).Length+ApmLocalization.T("개 · 복사 계층 ")+RigAnalysis.CopyTransforms(o).Count+ApmLocalization.T("개");
                } catch(Exception ex) {rigError=ex.Message;}
            }
            Repaint();
        }
        void ProjectChanged() { nextRefresh = 0; PollSource(); }
        void PollSource()
        {
            if(session && rigDirty && EditorApplication.timeSinceStartup>=nextRigRefresh) UpdateRigSummary();
            if (!session || EditorApplication.timeSinceStartup < nextRefresh) return;
            nextRefresh = EditorApplication.timeSinceStartup + (focusedWindow == this || meshEditor ? 2 : 10);
            Run(() => {
                if (session.RefreshSource())
                {
                    if(simpleMode) {session.SelectWholeMesh(); message=null;}
                    else message = ApmLocalization.T("원본 메쉬가 변경되어 선택과 미리보기를 초기화했습니다. 다시 선택해 주세요.");
                    slot = -1; Repaint();
                }
            });
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) DisposePreview();
        }
        void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { DisposePreview(); message = ex.Message; Repaint(); }
        }

        void OnGUI()
        {
            if (!session) return;
            float oldWidth=EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth=110;
            DrawHeader();
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginVertical(new GUIStyle {padding=new RectOffset(12,12,2,12)});
            EditorGUILayout.Space(6);
            BeginCard("1", ApmLocalization.T("원본"));
            EditorGUI.BeginChangeCheck();
            objectMode=EditorGUILayout.ToggleLeft(ApmLocalization.T("여러 오브젝트 각각 분리"),objectMode,EditorStyles.boldLabel);
            if(EditorGUI.EndChangeCheck()) {batchResults=null;message=null;report=null;DisposePreview();PickSelection();if(objectMode && meshEditor) meshEditor.Close();}
            if(objectMode)
            {
                DrawObjectMode();
                EditorGUILayout.EndVertical(); EditorGUILayout.EndScrollView(); EditorGUIUtility.labelWidth=oldWidth;
                return;
            }
            EditorGUILayout.Space(2);
            var source=(Renderer)EditorGUILayout.ObjectField(ApmLocalization.T("원본 메쉬"),session.options.source,typeof(Renderer),true);
            if(source!=session.options.source) Run(()=>{session.SetSource(source); slot=-1; message=null; if(simpleMode) session.SelectWholeMesh(); else if(source) OpenMeshEditor();});
            // Values only change on input events; snapshotting the session on every Layout/Repaint was costly with large selections.
            if(Event.current.type!=EventType.Layout && Event.current.type!=EventType.Repaint) Undo.RecordObject(session,ApmLocalization.T("악세사리 설정 변경"));
            var o=session.options;
            EditorGUI.BeginChangeCheck();
            o.avatarRoot=(Transform)EditorGUILayout.ObjectField(ApmLocalization.T("아바타"),o.avatarRoot,typeof(Transform),true);
            if(EditorGUI.EndChangeCheck()) { session.RefreshSource(); SelectionChanged(); }
            session.UpdateName();
            EndCard(); BeginCard("2", ApmLocalization.T("메쉬 선택"));
            if(simpleMode) DrawSimpleSelection(); else DrawSelection();
            EndCard();
            if(!simpleMode) {
            BeginCard("3", ApmLocalization.T("본 및 부착"));
            EditorGUI.BeginChangeCheck();
            o.mode=(ExtractionMode)GUILayout.Toolbar((int)o.mode,new[]{ApmLocalization.T("고정 악세사리"),ApmLocalization.T("본 포함 복사")},GUILayout.Height(25));
            if(o.mode==ExtractionMode.Skinned)
            {
                o.automaticBones=EditorGUILayout.ToggleLeft(ApmLocalization.T("웨이트로 본·부착 위치 자동 추적"),o.automaticBones);
                using(new EditorGUI.DisabledScope(o.automaticBones))
                    o.boneRoot=(Transform)EditorGUILayout.ObjectField(ApmLocalization.T("복사 본 루트"),o.boneRoot,typeof(Transform),true);
                if(o.automaticBones && !string.IsNullOrEmpty(rigSummary)) EditorGUILayout.LabelField(rigSummary,EditorStyles.miniLabel);
                if(o.automaticBones && !string.IsNullOrEmpty(rigError)) EditorGUILayout.HelpBox(rigError,MessageType.Warning);
            }
            using(new EditorGUI.DisabledScope(o.mode==ExtractionMode.Skinned && o.automaticBones))
                o.attachTarget=(Transform)EditorGUILayout.ObjectField(ApmLocalization.T("부착 대상"),o.attachTarget,typeof(Transform),true);
            o.useMA=EditorGUILayout.Toggle(ApmLocalization.T("MA 부착"),o.useMA);
            using(new EditorGUI.DisabledScope(!o.useMA || o.mode!=ExtractionMode.Skinned))
            using(new EditorGUI.IndentLevelScope())
                o.singleObject=EditorGUILayout.Toggle(ApmLocalization.T("본 하나면 오브젝트 1개로"),o.singleObject);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(ApmLocalization.T("크기 배율"));
            float scale=EditorGUILayout.FloatField(o.scale.x,GUILayout.Width(70));
            if(scale!=o.scale.x) o.scale=Vector3.one*scale;
            if(GUILayout.Button(ApmLocalization.T("초기화"),EditorStyles.miniButton,GUILayout.Width(50))) {o.positionOffset=Vector3.zero;o.rotationOffset=Vector3.zero;o.scale=Vector3.one;}
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
            advanced=EditorGUILayout.Foldout(advanced,ApmLocalization.T("위치·회전 / 고급 설정"),true);
            if(advanced)
            {
                o.positionOffset=CompactVector(ApmLocalization.T("위치"),o.positionOffset);
                o.rotationOffset=CompactVector(ApmLocalization.T("회전"),o.rotationOffset);
                o.excludeExternalColliders=EditorGUILayout.ToggleLeft(ApmLocalization.T("외부 Collider 제외"),o.excludeExternalColliders);
                EditorGUILayout.HelpBox(o.mode==ExtractionMode.Rigid?ApmLocalization.T("현재 편집 포즈를 고정 메쉬로 베이크합니다. 본 움직임과 쉐이프키 애니메이션은 유지하지 않습니다."):ApmLocalization.T("필요한 본·PhysBone·Constraint를 복사하고 새 본으로 연결합니다. 원본은 유지됩니다. 자동 추적을 끄면 본 루트와 부착 대상을 직접 지정할 수 있습니다. 범위 밖 참조와 Animator 기믹은 검사에서 확인하세요."),MessageType.None);
            }
            if(EditorGUI.EndChangeCheck()) SelectionChanged();
            EndCard();
            }
            BeginCard(simpleMode?"3":"4", ApmLocalization.T("출력 설정"));
            DrawOutput();
            if(!simpleMode) {showRecipe=EditorGUILayout.Foldout(showRecipe,ApmLocalization.T("선택 레시피"),true);
            if(showRecipe) DrawRecipe();}
            EditorGUILayout.Space(10);
            bool blocked=EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode();
            if(blocked) {DisposePreview(); EditorGUILayout.HelpBox(ApmLocalization.T("재생 / Animation Mode를 종료하세요."),MessageType.Warning);}
            using(new EditorGUI.DisabledScope(blocked||!o.source||o.faces.Count==0||string.IsNullOrWhiteSpace(o.name)||!AssetDatabase.IsValidFolder(ApmPreferences.OutputFolder)))
            {
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button(ApmLocalization.T("검사"),GUILayout.Width(70),GUILayout.Height(30))) Run(()=>{EnsureFresh(true);report=PrefabGenerator.Validate(o);message=null;});
                if(GUILayout.Button(ApmLocalization.T("미리보기"),GUILayout.Width(80),GUILayout.Height(30))) Run(CreatePreview);
                var previous=GUI.backgroundColor; GUI.backgroundColor=new Color(.42f,.78f,1f);
                if(GUILayout.Button(ApmLocalization.T("프리팹 저장"),GUILayout.Height(30))) Run(Save);
                GUI.backgroundColor=previous;
                EditorGUILayout.EndHorizontal();
            }
            instantiate=EditorGUILayout.ToggleLeft(ApmLocalization.T("저장 후 씬에 추가"),instantiate);
            EditorGUILayout.LabelField(ApmLocalization.T("원본은 유지됩니다. 선택·씬 배치는 Ctrl+Z로 되돌릴 수 있습니다."),EditorStyles.wordWrappedMiniLabel);
            EndCard();
            DrawReport();
            if(!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message,MessageType.Warning);
            if(previewRenderer!=null) DrawPreview();
            EditorGUILayout.EndVertical(); EditorGUILayout.EndScrollView(); EditorGUIUtility.labelWidth=oldWidth;
        }
        void DrawHeader()
        {
            titleContent=new GUIContent(ApmLocalization.T("악세사리 분리 툴"),ApmLogo.Icon);
            EditorGUILayout.BeginVertical(new GUIStyle {padding=new RectOffset(16,16,14,10)});
            ApmLogo.Banner(position.width-32);
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("FORMORI · v"+ApmVersion.Current,EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            int language=EditorGUILayout.Popup(ApmLocalization.Language,new[]{"한국어","English","日本語"},GUILayout.Width(90));
            if(language!=ApmLocalization.Language) {ApmLocalization.Language=language; report=null; message=null; rigDirty=true; if(meshEditor) meshEditor.Repaint();}
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
            bool next=GUILayout.Toolbar(simpleMode?0:1,new[]{ApmLocalization.T("간단"),ApmLocalization.T("심화")},GUILayout.Height(26))==0;
            if(next!=simpleMode) Run(()=>SetSimpleMode(next));
            if(GUILayout.Button(ApmLocalization.T("여러 오브젝트를 한 프리팹으로 분리")+" ↗",EditorStyles.miniButton)) ApmMultiMeshWindow.Open();
            EditorGUILayout.EndVertical();
            var line=GUILayoutUtility.GetRect(1,2,GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(line,EditorGUIUtility.isProSkin?new Color(1,1,1,.08f):new Color(0,0,0,.1f));
        }
        void PickSelection()
        {
            pickedRenderers=BatchExtractor.CollectRenderers(Selection.objects);
            if(objectMode) {batchResults=null;Repaint();}
        }
        void DrawObjectMode()
        {
            EditorGUILayout.HelpBox(ApmLocalization.T("Hierarchy에서 분리할 오브젝트를 선택하세요. 여러 개를 선택할 수 있고, 자식 메쉬도 포함됩니다. 메쉬마다 악세사리 프리팹을 하나씩 만듭니다."),MessageType.None);
            if(pickedRenderers.Count==0) EditorGUILayout.HelpBox(ApmLocalization.T("선택한 오브젝트에 메쉬가 없습니다."),MessageType.Info);
            else
            {
                EditorGUILayout.LabelField(ApmLocalization.T("선택된 메쉬 ")+pickedRenderers.Count+ApmLocalization.T("개"),EditorStyles.boldLabel);
                using(new EditorGUI.DisabledScope(true))
                    foreach(var r in pickedRenderers.Take(8)) EditorGUILayout.ObjectField(r,typeof(Renderer),true);
                if(pickedRenderers.Count>8) EditorGUILayout.LabelField(ApmLocalization.T("외 ")+(pickedRenderers.Count-8)+ApmLocalization.T("개"),EditorStyles.miniLabel);
            }
            EndCard();
            BeginCard("2", ApmLocalization.T("출력 설정"));
            EditorGUILayout.LabelField(ApmLocalization.T("프리팹 이름은 각 메쉬 오브젝트 이름을 씁니다. 본·PhysBone·Constraint는 간단 모드처럼 자동으로 추적합니다."),EditorStyles.wordWrappedMiniLabel);
            DrawPlacement();
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            var folder=AssetDatabase.LoadAssetAtPath<DefaultAsset>(ApmPreferences.OutputFolder);
            var chosen=(DefaultAsset)EditorGUILayout.ObjectField(ApmLocalization.T("저장 폴더"),folder,typeof(DefaultAsset),false);
            if(chosen!=folder && chosen) Run(()=>ApmPreferences.SetOutputFolder(AssetDatabase.GetAssetPath(chosen)));
            if(GUILayout.Button(ApmLocalization.T("찾기"),GUILayout.Width(54))) Run(()=>{
                var path=EditorUtility.OpenFolderPanel(ApmLocalization.T("저장 폴더"),System.IO.Path.GetFullPath(ApmPreferences.OutputFolder),"");
                if(!string.IsNullOrEmpty(path)) ApmPreferences.SetOutputFolder(path);
            });
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(ApmPreferences.OutputFolder,EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(10);
            bool blocked=EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode();
            if(blocked) EditorGUILayout.HelpBox(ApmLocalization.T("재생 / Animation Mode를 종료하세요."),MessageType.Warning);
            using(new EditorGUI.DisabledScope(blocked||pickedRenderers.Count==0||!AssetDatabase.IsValidFolder(ApmPreferences.OutputFolder)))
            {
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button(ApmLocalization.T("검사"),GUILayout.Width(70),GUILayout.Height(30))) Run(()=>{batchResults=BatchExtractor.ValidateEach(pickedRenderers);message=null;});
                var previous=GUI.backgroundColor; GUI.backgroundColor=new Color(.42f,.78f,1f);
                if(GUILayout.Button(ApmLocalization.T("프리팹 저장")+" ("+pickedRenderers.Count+")",GUILayout.Height(30))) Run(SaveEach);
                GUI.backgroundColor=previous;
                EditorGUILayout.EndHorizontal();
            }
            instantiate=EditorGUILayout.ToggleLeft(ApmLocalization.T("저장 후 씬에 추가"),instantiate);
            EditorGUILayout.LabelField(ApmLocalization.T("원본은 유지됩니다. 선택·씬 배치는 Ctrl+Z로 되돌릴 수 있습니다."),EditorStyles.wordWrappedMiniLabel);
            EndCard();
            if(!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message,batchResults!=null && batchResults.Any(r=>!string.IsNullOrEmpty(r.error))?MessageType.Warning:MessageType.Info);
            DrawBatchResults();
        }
        void SaveEach()
        {
            var sources=pickedRenderers.ToList();
            try { batchResults=BatchExtractor.ExtractEach(sources,ApmPreferences.OutputFolder,(i,n,name)=>EditorUtility.DisplayProgressBar(ApmLocalization.T("악세사리 분리 툴"),name+" ("+(i+1)+"/"+n+")",(float)i/n),instantiate,session.options.placement); }
            finally { EditorUtility.ClearProgressBar(); }

            int ok=batchResults.Count(r=>r.Succeeded);
            message=ApmLocalization.T("저장 완료: ")+ok+" / "+batchResults.Count;
            var first=batchResults.FirstOrDefault(r=>r.Succeeded);
            if(first!=null) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(first.path));
        }
        void DrawBatchResults()
        {
            if(batchResults==null || batchResults.Count==0) return;
            foreach(var item in batchResults)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                bool ok=string.IsNullOrEmpty(item.error);
                GUILayout.Label(ok?"✔":"✖",GUILayout.Width(16));
                GUILayout.Label(item.sourceName,EditorStyles.boldLabel,GUILayout.Width(140));
                if(!ok) GUILayout.Label(item.error,EditorStyles.wordWrappedMiniLabel);
                else
                {
                    GUILayout.Label((item.Succeeded ? System.IO.Path.GetFileNameWithoutExtension(item.path) : ApmLocalization.T("검사 통과"))+(item.warnings>0?"  ⚠"+item.warnings:""),EditorStyles.miniLabel);
                    if(item.Succeeded && GUILayout.Button(ApmLocalization.T("보기"),EditorStyles.miniButton,GUILayout.Width(44))) {var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(item.path);Selection.activeObject=prefab;EditorGUIUtility.PingObject(prefab);}
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        static void BeginCard(string number,string title)
        {
            EditorGUILayout.BeginVertical(new GUIStyle(EditorStyles.helpBox) {padding=new RectOffset(12,12,10,10)});
            GUILayout.Label(number+". "+ApmLocalization.T(title),EditorStyles.boldLabel);
            GUILayout.Space(7);
        }
        static void EndCard() {EditorGUILayout.EndVertical();GUILayout.Space(8);}
        // Where the saved prefab sits: on the original (follows its bone) or on the ground at the centre.
        void DrawPlacement()
        {
            var o=session.options;
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(ApmLocalization.T("결과 위치"));
            var next=(PlacementMode)GUILayout.Toolbar((int)o.placement,new[]{ApmLocalization.T("원본 따라가기"),ApmLocalization.T("바닥(원점)에 놓기")});
            EditorGUILayout.EndHorizontal();
            if(next!=o.placement) {o.placement=next;SelectionChanged();}
            EditorGUILayout.LabelField(o.placement==PlacementMode.FollowSource
                ?ApmLocalization.T("원본과 같은 자리에 붙어 원본 본을 따라 움직입니다.")
                :ApmLocalization.T("원본 위치를 무시하고 바닥 중앙(아바타 발밑, 아바타가 없으면 월드 원점)에 반듯하게 놓습니다. 원본 본을 따라가지 않습니다."),EditorStyles.wordWrappedMiniLabel);
            if(o.placement==PlacementMode.Ground && (o.useMA || o.bodyRig)) EditorGUILayout.HelpBox(ApmLocalization.T("MA 부착·몸 관절 메쉬는 아바타에 붙어야 동작하므로 원본 따라가기로 저장합니다."),MessageType.Info);
        }
        void DrawOutput()
        {
            var o=session.options;
            EditorGUI.BeginChangeCheck();
            o.useCustomName=GUILayout.Toolbar(o.useCustomName?1:0,new[]{ApmLocalization.T("자동 이름"),ApmLocalization.T("직접 입력")},GUILayout.Height(24))==1;
            session.UpdateName();
            string editedName=EditorGUILayout.TextField(ApmLocalization.T("프리팹 이름"),o.name);
            if(editedName!=o.name)
            {
                o.useCustomName=true;
                o.customName=editedName;
                session.UpdateName();
            }
            if(EditorGUI.EndChangeCheck()) SelectionChanged();
            if(string.IsNullOrWhiteSpace(o.name)) EditorGUILayout.HelpBox(ApmLocalization.T("프리팹 이름을 입력하세요."),MessageType.Info);
            DrawPlacement();
            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            var folder=AssetDatabase.LoadAssetAtPath<DefaultAsset>(ApmPreferences.OutputFolder);
            var chosen=(DefaultAsset)EditorGUILayout.ObjectField(ApmLocalization.T("저장 폴더"),folder,typeof(DefaultAsset),false);
            if(chosen!=folder && chosen) Run(()=>ApmPreferences.SetOutputFolder(AssetDatabase.GetAssetPath(chosen)));
            if(GUILayout.Button(ApmLocalization.T("찾기"),GUILayout.Width(54))) Run(()=>{
                var path=EditorUtility.OpenFolderPanel(ApmLocalization.T("저장 폴더"),System.IO.Path.GetFullPath(ApmPreferences.OutputFolder),"");
                if(!string.IsNullOrEmpty(path)) ApmPreferences.SetOutputFolder(path);
            });
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(ApmPreferences.OutputFolder,EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(ApmLocalization.T("이 폴더 안에 이름별 하위 폴더를 만듭니다."),EditorStyles.wordWrappedMiniLabel);
        }
        static Vector3 CompactVector(string label,Vector3 value)
        {
            EditorGUILayout.BeginHorizontal(); GUILayout.Label(label,GUILayout.Width(65));
            for(int i=0;i<3;i++) {GUILayout.Label(new[]{"X","Y","Z"}[i],GUILayout.Width(12));value[i]=EditorGUILayout.FloatField(value[i],GUILayout.Width(52));}
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal(); return value;
        }
        public void OpenMeshEditor()
        {
            if(!session || !session.mesh) return;
            EnsureFresh();
            if(!meshEditor) {meshEditor=CreateInstance<ApmMeshEditor>();meshEditor.position=new Rect(position.xMax+15,position.y,1000,720);meshEditor.Show();}
            meshEditor.Bind(session);meshEditor.Focus();
        }
        void SetSimpleMode(bool value)
        {
            simpleMode=value; message=null;
            if(simpleMode)
            {
                if(meshEditor) meshEditor.Close();
                if(session.options.source) {advancedBackup=ApmSession.Clone(session.options);advancedFingerprint=session.FingerprintValue;}
                session.SelectWholeMesh();
            }
            else if(advancedBackup!=null && advancedBackup.source && advancedBackup.source==session.options.source)
            {
                // Switching back restores the partial selection and manual settings instead of keeping "whole mesh".
                if(!session.RestoreState(ApmSession.Clone(advancedBackup),advancedFingerprint,true)) message=ApmLocalization.T("원본 메쉬가 변경되어 이전 심화 선택을 복원하지 못했습니다.");
            }
            scroll=Vector2.zero; SelectionChanged();
        }
        void DrawSimpleSelection()
        {
            EditorGUILayout.LabelField(ApmLocalization.T("메쉬 전체 자동 선택"),EditorStyles.boldLabel);
            EditorGUILayout.LabelField(session.options.faces.Count+" / "+session.topology.Length+ApmLocalization.T(" 면"),EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(ApmLocalization.T("전체 메쉬와 연결된 본·PhysBone·Constraint를 새 프리팹으로 복사합니다."),MessageType.None);
            if(!string.IsNullOrEmpty(rigSummary)) EditorGUILayout.LabelField(rigSummary,EditorStyles.wordWrappedMiniLabel);
            if(!string.IsNullOrEmpty(rigError)) EditorGUILayout.HelpBox(rigError,MessageType.Warning);
            if(session.options.source is MeshRenderer) EditorGUILayout.HelpBox(ApmLocalization.T("본 없는 메쉬는 고정 메쉬로 복사합니다."),MessageType.Info);
            EditorGUI.BeginChangeCheck();
            using(new EditorGUI.DisabledScope(!(session.options.source is SkinnedMeshRenderer)))
                session.options.useMA=EditorGUILayout.Toggle(ApmLocalization.T("MA 부착"),session.options.useMA);
            using(new EditorGUI.DisabledScope(!session.options.useMA || !(session.options.source is SkinnedMeshRenderer)))
            using(new EditorGUI.IndentLevelScope())
                session.options.singleObject=EditorGUILayout.Toggle(ApmLocalization.T("본 하나면 오브젝트 1개로"),session.options.singleObject);
            if(EditorGUI.EndChangeCheck()) SelectionChanged();
        }
        void DrawSelection()
        {
            using(new EditorGUI.DisabledScope(!session.mesh))
                if(GUILayout.Button(ApmLocalization.T("메쉬 편집  ↗"),GUILayout.Height(36))) Run(OpenMeshEditor);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(ApmLocalization.T("선택 ")+session.options.faces.Count+" / "+session.topology.Length+ApmLocalization.T(" 면"),EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using(new EditorGUI.DisabledScope(!session.mesh))
            {
                if(GUILayout.Button(ApmLocalization.T("전체"),EditorStyles.miniButtonLeft,GUILayout.Width(45))) Apply(AllVisible(),false,false);
                if(GUILayout.Button(ApmLocalization.T("해제"),EditorStyles.miniButtonRight,GUILayout.Width(45))) Apply(new int[0],false,false);
            }
            EditorGUILayout.EndHorizontal();
            showUV=EditorGUILayout.Foldout(showUV,ApmLocalization.T("UV로 선택"),true);
            if(!showUV||!session.mesh) return;
            islands=GUILayout.Toolbar(islands?1:0,new[]{ApmLocalization.T("면"),ApmLocalization.T("UV 아일랜드")})==1;
            var labels=new string[session.mesh.subMeshCount+1];labels[0]=ApmLocalization.T("전체 슬롯");
            for(int i=1;i<labels.Length;i++)labels[i]=ApmLocalization.T("슬롯 ")+(i-1);
            slot=EditorGUILayout.Popup(ApmLocalization.T("머티리얼"),slot+1,labels)-1;
            DrawUV();
            if(overlap.Length>1)
            {
                EditorGUILayout.HelpBox(ApmLocalization.T("겹친 UV: 선택할 면을 고르세요."),MessageType.Info);
                foreach(int face in overlap)
                    if(GUILayout.Button(ApmLocalization.T("면 ")+face+ApmLocalization.T(" · 슬롯 ")+session.topology[face].submesh)) {Pick(face,true,overlapAdd,overlapRemove);break;}
            }
        }
        bool Visible(int id) { return slot < 0 || session.topology[id].submesh == slot; }
        IEnumerable<int> AllVisible()
        {
            for (int i = 0; i < session.topology.Length; i++) if (Visible(i)) yield return i;
        }
        void Apply(IEnumerable<int> ids, bool add, bool remove) { Run(() => { EnsureFresh(); session.ApplySelection(ids, add, remove); }); }
        void Pick(int id, bool uv, bool add, bool remove)
        {
            Run(() => { EnsureFresh(); session.ApplySelection(islands ? session.Island(id, uv, slot) : new[] { id }, add, remove); });
        }
        Vector2 ToUV(Vector2 p, Rect area)
        {
            return new Vector2(uvBounds.xMin + (p.x - area.xMin) / area.width * uvBounds.width,
                uvBounds.yMax - (p.y - area.yMin) / area.height * uvBounds.height);
        }
        Vector3 FromUV(Vector2 p, Rect area)
        {
            return new Vector3(area.xMin + (p.x - uvBounds.xMin) / uvBounds.width * area.width,
                area.yMin + (uvBounds.yMax - p.y) / uvBounds.height * area.height, 0);
        }
        void DrawUV()
        {
            if (session.uv.Length != session.vertices.Length)
            { EditorGUILayout.HelpBox(ApmLocalization.T("UV0가 없어 Scene 선택을 사용해야 합니다."), MessageType.Info); return; }
            Rect area = GUILayoutUtility.GetRect(100, 300, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(area, new Color(.12f, .12f, .12f));
            if (boundedUV != session.uv)
            {
            boundedUV = session.uv;
            Vector2 min = Vector2.zero, max = Vector2.one;
            foreach (var p in session.uv) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Vector2 size = max - min; float extent = Mathf.Max(size.x, size.y) * 1.08f;
            Vector2 center = (min + max) * .5f;
            uvBounds = new Rect(center - Vector2.one * extent * .5f, Vector2.one * extent);
            }
            if (Event.current.type == EventType.Repaint)
            {
                var materials = session.options.source.sharedMaterials;
                int materialSlot = Mathf.Max(0, slot);
                Texture texture = materialSlot < materials.Length && materials[materialSlot] ? materials[materialSlot].mainTexture : null;
                if (texture)
                {
                    Vector3 tl = FromUV(new Vector2(0, 1), area), br = FromUV(new Vector2(1, 0), area);
                    GUI.DrawTexture(Rect.MinMaxRect(tl.x, tl.y, br.x, br.y), texture, ScaleMode.StretchToFill);
                }
                // One batched GL pass; per-triangle Handles calls stalled the window on 50k-face outfits.
                // GL ignores the scroll view's clipping, so it draws into a texture that GUI.DrawTexture then clips;
                // drawing straight to the window painted the UVs over the header once the panel scrolled past the top.
                float ppp = EditorGUIUtility.pixelsPerPoint;
                int rw = Mathf.Max(1, Mathf.RoundToInt(area.width * ppp)), rh = Mathf.Max(1, Mathf.RoundToInt(area.height * ppp));
                if (!uvTarget || uvTarget.width != rw || uvTarget.height != rh)
                {
                    if (uvTarget) { uvTarget.Release(); DestroyImmediate(uvTarget); }
                    uvTarget = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4 };
                }
                var previousTarget = RenderTexture.active; RenderTexture.active = uvTarget;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, area.width, area.height, 0);
                UVMaterial().SetPass(0);
                var local = new Rect(0, 0, area.width, area.height);
                GL.Begin(GL.TRIANGLES); GL.Color(new Color(1, .5f, .05f, .55f));
                foreach (int id in session.selected)
                {
                    if (id >= session.topology.Length || !Visible(id)) continue;
                    var f = session.topology[id];
                    GL.Vertex(FromUV(session.uv[f.a], local)); GL.Vertex(FromUV(session.uv[f.b], local)); GL.Vertex(FromUV(session.uv[f.c], local));
                }
                GL.End();
                GL.Begin(GL.LINES); GL.Color(new Color(.8f, .9f, 1, .45f));
                for (int id = 0; id < session.topology.Length; id++)
                {
                    if (!Visible(id)) continue;
                    var f = session.topology[id];
                    triangle[0] = FromUV(session.uv[f.a], local); triangle[1] = FromUV(session.uv[f.b], local); triangle[2] = FromUV(session.uv[f.c], local);
                    GL.Vertex(triangle[0]); GL.Vertex(triangle[1]); GL.Vertex(triangle[1]); GL.Vertex(triangle[2]); GL.Vertex(triangle[2]); GL.Vertex(triangle[0]);
                }
                GL.End();
                GL.PopMatrix();
                RenderTexture.active = previousTarget;
                GUI.DrawTexture(area, uvTarget, ScaleMode.StretchToFill, true);
                if (dragging) EditorGUI.DrawRect(Rect.MinMaxRect(Mathf.Min(dragStart.x, dragEnd.x), Mathf.Min(dragStart.y, dragEnd.y), Mathf.Max(dragStart.x, dragEnd.x), Mathf.Max(dragStart.y, dragEnd.y)), new Color(.2f, .7f, 1, .3f));
            }
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && area.Contains(e.mousePosition))
            {
                dragging = true; dragStart = dragEnd = e.mousePosition; dragAdd = e.shift; dragRemove = e.control || e.command; e.Use();
            }
            else if (dragging && e.type == EventType.MouseDrag) { dragEnd = e.mousePosition; Repaint(); e.Use(); }
            else if (dragging && e.type == EventType.MouseUp && e.button == 0)
            {
                dragging = false; dragEnd = e.mousePosition;
                Run(() => {
                    EnsureFresh();
                    if ((dragStart - dragEnd).sqrMagnitude > 16)
                    {
                        var a = ToUV(dragStart, area); var b = ToUV(dragEnd, area);
                        var ids = MeshSelection.InUVRectFaces(session.topology, session.uv, session.vertices.Length, Rect.MinMaxRect(Mathf.Min(a.x,b.x), Mathf.Min(a.y,b.y), Mathf.Max(a.x,b.x), Mathf.Max(a.y,b.y)), slot);
                        var expanded = new HashSet<int>();
                        if (islands)
                        {
                            // Island labels are computed once per revision, so any number of seeds costs one pass.
                            var labels = session.IslandLabels(true, slot); var hit = new HashSet<int>();
                            foreach (int id in ids) if (labels[id] >= 0) hit.Add(labels[id]);
                            for (int i = 0; i < labels.Length; i++) if (labels[i] >= 0 && hit.Contains(labels[i])) expanded.Add(i);
                        }
                        else expanded.UnionWith(ids);
                        session.ApplySelection(expanded, dragAdd, dragRemove);
                    }
                    else
                    {
                        overlap = MeshSelection.PickUVFaces(session.topology, session.uv, session.vertices.Length, ToUV(dragEnd, area), slot);
                        overlapAdd = dragAdd; overlapRemove = dragRemove;
                        if (overlap.Length == 1) Pick(overlap[0], true, dragAdd, dragRemove);
                        else if (overlap.Length == 0 && !dragAdd && !dragRemove) session.ApplySelection(new int[0], false, false);
                    }
                });
                Repaint(); e.Use();
            }
        }

        RenderTexture uvTarget;
        static Material uvMaterial;
        static Material UVMaterial()
        {
            if (uvMaterial) return uvMaterial;
            uvMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            uvMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha); uvMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            uvMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off); uvMaterial.SetInt("_ZWrite", 0); uvMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            uvMaterial.SetColor("_Color", Color.white);
            return uvMaterial;
        }

        void DrawRecipe()
        {
            recipe = (ApmRecipe)EditorGUILayout.ObjectField(ApmLocalization.T("선택 레시피"), recipe, typeof(ApmRecipe), false);
            using (new EditorGUI.DisabledScope(!recipe))
                if (GUILayout.Button(ApmLocalization.T("현재 원본에 레시피 불러오기"))) Run(() => {
                    if (!recipe.TryRestore(session.options.source, session.options.avatarRoot, out ApmOptions restored, out string error)) throw new InvalidOperationException(error);
                    session.Restore(restored); message = ApmLocalization.T("레시피를 불러왔습니다.");
                });
        }
        // full: hash the whole mesh. Selection clicks use the cheap check; validate/preview/save must be exact.
        void EnsureFresh(bool full=false)
        {
            bool stale=full ? session.RefreshSourceFull() : session.RefreshSource();
            if(simpleMode) session.SelectWholeMesh();
            else if(stale) throw new InvalidOperationException(ApmLocalization.T("원본이 변경되었습니다. 면을 다시 선택하세요."));
        }
        void CreatePreview()
        {
            EnsureFresh(true); DisposePreview(); report = PrefabGenerator.Validate(session.options);
            if (!report.IsValid) return;
            preview = PrefabGenerator.Preview(session.options);
            previewRenderer = new PreviewRenderUtility();
            foreach (var t in preview.root.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            previewRenderer.AddSingleGO(preview.root);
            var renderers = preview.root.GetComponentsInChildren<Renderer>();
            Bounds bounds = new Bounds(preview.root.transform.position, Vector3.one * .1f);
            if (renderers.Length > 0) { bounds = MeshSelection.GeometryBounds(renderers[0]); foreach (var r in renderers) bounds.Encapsulate(MeshSelection.GeometryBounds(r)); }
            if (compareSource)
            {
                var source = session.options.source;
                comparisonMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                if (source is SkinnedMeshRenderer skin) skin.BakeMesh(comparisonMesh, false);
                else { DestroyImmediate(comparisonMesh); comparisonMesh = Instantiate(session.mesh); comparisonMesh.hideFlags = HideFlags.HideAndDontSave; }
                var comparison = new GameObject("원본 비교", typeof(MeshFilter), typeof(MeshRenderer));
                comparison.hideFlags = HideFlags.HideAndDontSave;
                comparison.GetComponent<MeshFilter>().sharedMesh = comparisonMesh;
                var comparisonRenderer = comparison.GetComponent<MeshRenderer>();
                comparisonRenderer.sharedMaterials = source.sharedMaterials;
                comparison.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                comparison.transform.localScale = source.transform.lossyScale;
                previewRenderer.AddSingleGO(comparison);
                float spacing = comparisonRenderer.bounds.extents.x + bounds.extents.x + Mathf.Max(.01f, bounds.size.x * .2f);
                comparison.transform.position += Vector3.left * spacing;
                bounds.Encapsulate(MeshSelection.GeometryBounds(comparisonRenderer));
            }
            previewCenter = bounds.center;
            previewDistance = Mathf.Max(.02f, bounds.extents.magnitude) * 4.6f;
            UpdatePreviewCamera();
            previewRenderer.lights[0].intensity = 1.2f; previewRenderer.lights[0].transform.rotation = Quaternion.Euler(40, 40, 0);
            message = null;
        }
        void DrawPreview()
        {
            EditorGUILayout.LabelField(ApmLocalization.T("분리 결과 미리보기 (격리된 프리뷰 공간)"));
            bool nextCompare = EditorGUILayout.ToggleLeft(ApmLocalization.T("원본을 왼쪽에 나란히 표시"), compareSource);
            if (nextCompare != compareSource) { compareSource = nextCompare; Run(CreatePreview); }
            EditorGUILayout.LabelField(ApmLocalization.T("드래그: 회전 / 휠: 확대·축소"), EditorStyles.miniLabel);
            Rect rect = GUILayoutUtility.GetRect(100, 260, GUILayout.ExpandWidth(true));
            var e = Event.current;
            if (rect.Contains(e.mousePosition) && previewRenderer != null)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    previewOrbit += e.delta * .5f; previewOrbit.y = Mathf.Clamp(previewOrbit.y, -85, 85);
                    UpdatePreviewCamera(); e.Use(); Repaint();
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    previewDistance = Mathf.Max(.005f, previewDistance * Mathf.Exp(e.delta.y * .07f));
                    UpdatePreviewCamera(); e.Use(); Repaint();
                }
            }
            if (previewRenderer != null && Event.current.type == EventType.Repaint)
                Run(() => { previewRenderer.BeginPreview(rect, GUIStyle.none); previewRenderer.Render(); previewRenderer.EndAndDrawPreview(rect); });
            if (GUILayout.Button(ApmLocalization.T("미리보기 닫기"))) DisposePreview();
        }
        void DisposePreview()
        {
            if (previewRenderer != null) { previewRenderer.Cleanup(); previewRenderer = null; }
            if (preview != null) { preview.Dispose(); preview = null; }
            if (comparisonMesh) { DestroyImmediate(comparisonMesh); comparisonMesh = null; }
        }
        void UpdatePreviewCamera()
        {
            var camera = previewRenderer.camera;
            camera.transform.position = previewCenter + Quaternion.Euler(-previewOrbit.y, previewOrbit.x, 0) * Vector3.forward * previewDistance;
            camera.transform.LookAt(previewCenter);
            camera.nearClipPlane = previewDistance / 1000;
            camera.farClipPlane = previewDistance * 100;
        }
        void DrawReport()
        {
            if (report == null) return;
            foreach (var text in report.errors) EditorGUILayout.HelpBox(text, MessageType.Error);
            foreach (var text in report.warnings) EditorGUILayout.HelpBox(text, MessageType.Warning);
            foreach (var text in report.details) EditorGUILayout.HelpBox(text, MessageType.None);
            if (report.IsValid) EditorGUILayout.HelpBox(ApmLocalization.T("검사 통과: 생성 가능합니다."), MessageType.Info);
        }
        void Save()
        {
            EnsureFresh(true); DisposePreview(); report = PrefabGenerator.Validate(session.options);
            if (!report.IsValid) return;
            string path = PrefabGenerator.Save(session.options, ApmPreferences.OutputFolder);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab);
            if (instantiate)
            {
                Selection.activeGameObject = InstantiateResult(prefab, session.options);
            }
            message = ApmLocalization.T("저장 완료: ") + path;
        }

        public static GameObject InstantiateResult(GameObject prefab, ApmOptions options)
        {
            if (!prefab || options == null || !options.source) throw new ArgumentException(ApmLocalization.T("프리팹과 원본 Renderer를 지정하세요."));
            if (options.placement == PlacementMode.Ground && PrefabGenerator.CanGround(prefab))
            {
                // On the ground: at the avatar's feet, or the world origin without an avatar.
                var grounded = (GameObject)PrefabUtility.InstantiatePrefab(prefab, options.avatarRoot);
                Undo.RegisterCreatedObjectUndo(grounded, ApmLocalization.T("악세사리 프리팹 추가"));
                grounded.transform.localPosition = Vector3.zero; grounded.transform.localRotation = Quaternion.identity; grounded.transform.localScale = Vector3.one;
                return grounded;
            }
            Transform parent = options.useMA ? options.avatarRoot : options.attachTarget;
            if (!parent) parent = options.avatarRoot;
            var validation = new ValidationReport();
            if (parent) RigAnalysis.ValidateScale(parent, validation);
            if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.errors));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            Undo.RegisterCreatedObjectUndo(instance, ApmLocalization.T("악세사리 프리팹 추가"));
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            if (!options.useMA && !options.attachTarget)
            {
                var anchor = options.source.transform;
                instance.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
                Vector3 parentScale = parent ? parent.lossyScale : Vector3.one;
                Vector3 scale = anchor.lossyScale;
                instance.transform.localScale = new Vector3(scale.x / parentScale.x, scale.y / parentScale.y, scale.z / parentScale.z);
            }
            return instance;
        }
    }
}


