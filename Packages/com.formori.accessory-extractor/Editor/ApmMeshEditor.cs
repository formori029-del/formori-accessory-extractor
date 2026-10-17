using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace AccessoryPrefabMaker
{
    // A real SceneView with its own disposable scene. The original avatar is never edited.
    public sealed class ApmMeshEditor : SceneView
    {
        ApmSession session;
        Scene isolated;
        GameObject display;
        Mesh mesh, overlay;
        MeshCollider picker;
        Material tint;
        Vector3[] centers, projected;
        int[] slots;
        Mesh cachedSource;
        Renderer cachedRenderer;
        int cachedRevision;
        Matrix4x4 sourceMatrix;
        int mode, slot = -1;
        bool through, wire, dragging, adding, removing;
        float radius = 24;
        Vector2 start, last, cursor;
        int undoGroup = -1;
        int[] beforeStroke;
        string error;
        bool projectedValid;
        SceneView previousActive;
        static readonly FieldInfo LastActive = typeof(SceneView).GetField("s_LastActiveSceneView", BindingFlags.Static|BindingFlags.NonPublic);
        static readonly PropertyInfo CullingMask = typeof(SceneView).GetProperty("overrideSceneCullingMask", BindingFlags.Instance|BindingFlags.NonPublic);

        public override void OnEnable()
        {
            base.OnEnable();
            titleContent = new GUIContent(ApmLocalization.T("악세사리 분리 툴 · 메쉬 편집"));
            minSize = new Vector2(860,440);
            wantsMouseMove = true;
            duringSceneGui += DrawEditor;
            beforeSceneGui += ProtectOriginal;
            Undo.undoRedoPerformed += RebuildOverlay;
            AssemblyReloadEvents.beforeAssemblyReload += CloseForReload;
            sceneLighting = false;
            showGrid = false;
            if (lastActiveSceneView != this) previousActive = lastActiveSceneView;
            // A layout restored after an editor restart has no session to edit: close instead of showing the avatar scene.
            EditorApplication.delayCall += () => { if (this && !session) Close(); };
        }
        public override void OnDisable()
        {
            duringSceneGui -= DrawEditor;
            beforeSceneGui -= ProtectOriginal;
            Undo.undoRedoPerformed -= RebuildOverlay;
            AssemblyReloadEvents.beforeAssemblyReload -= CloseForReload;
            FinishStroke();
            RestoreTools();
            Cleanup();
            base.OnDisable();
        }
        public void Bind(ApmSession value)
        {
            FinishStroke(); session=value; slot=-1; error=null;
            Rebuild(); FrameSource();
        }
        void CloseForReload()
        {
            // Reload callbacks can retain a window already closed by another owner,
            // or a test-created SceneView that was never attached to a native window.
            if(!this) return;
            if(rootVisualElement.panel!=null) Close(); else DestroyImmediate(this);
        }
        public void SelectionUpdated()
        {
            if (!session || !session.options.source || !session.mesh) { Cleanup(); Repaint(); return; }
            if (cachedSource != session.mesh || cachedRenderer != session.options.source || cachedRevision != session.revision) Rebuild(); else RebuildOverlay();
        }
        void Cleanup()
        {
            if(camera) { customScene=default(Scene); if(CullingMask!=null) CullingMask.SetValue(this,0UL); }
            if (display) DestroyImmediate(display);
            if (mesh) DestroyImmediate(mesh);
            if (overlay) DestroyImmediate(overlay);
            if (tint) DestroyImmediate(tint);
            if (isolated.IsValid()) EditorSceneManager.ClosePreviewScene(isolated);
            display=null; mesh=null; overlay=null; tint=null; picker=null; cachedSource=null;
        }
        void Rebuild()
        {
            Cleanup();
            if (!session || !session.options.source || !session.mesh) return;
            if (CullingMask == null) { error=ApmLocalization.T("이 Unity 버전은 독립 Scene 편집 창을 지원하지 않습니다."); return; }
            try
            {
                isolated=EditorSceneManager.NewPreviewScene();
                customScene=isolated;
                CullingMask.SetValue(this,EditorSceneManager.GetSceneCullingMask(isolated));
                var source=session.options.source;
                if(slot>=session.mesh.subMeshCount) slot=-1;
                if (source is SkinnedMeshRenderer skin) { mesh=new Mesh(); skin.BakeMesh(mesh,false); }
                else mesh=Instantiate(session.mesh);
                mesh.hideFlags=HideFlags.HideAndDontSave;
                var vertices=mesh.vertices;
                sourceMatrix=source.transform.localToWorldMatrix;
                for(int i=0;i<vertices.Length;i++) vertices[i]=sourceMatrix.MultiplyPoint3x4(vertices[i]);
                var normals=mesh.normals;
                var normalMatrix=sourceMatrix.inverse.transpose;
                for(int i=0;i<normals.Length;i++) normals[i]=normalMatrix.MultiplyVector(normals[i]).normalized;
                mesh.vertices=vertices; mesh.normals=normals;
                var tangents=mesh.tangents;
                for(int i=0;i<tangents.Length;i++) { var t=sourceMatrix.MultiplyVector(tangents[i]).normalized; tangents[i]=new Vector4(t.x,t.y,t.z,tangents[i].w * (sourceMatrix.determinant<0 ? -1 : 1)); }
                mesh.tangents=tangents; mesh.RecalculateBounds();
                display=new GameObject("Accessory edit preview",typeof(MeshFilter),typeof(MeshRenderer),typeof(MeshCollider));
                display.hideFlags=HideFlags.HideAndDontSave|HideFlags.NotEditable;
                SceneManager.MoveGameObjectToScene(display,isolated);
                display.GetComponent<MeshFilter>().sharedMesh=mesh;
                display.GetComponent<MeshRenderer>().sharedMaterials=source.sharedMaterials;
                picker=display.GetComponent<MeshCollider>(); picker.sharedMesh=mesh;
                var faces=session.topology;
                centers=new Vector3[faces.Length]; projected=new Vector3[faces.Length]; slots=new int[faces.Length];
                for(int i=0;i<faces.Length;i++) { var f=faces[i]; centers[i]=(vertices[f.a]+vertices[f.b]+vertices[f.c])/3; slots[i]=f.submesh; }
                overlay=new Mesh {indexFormat=mesh.indexFormat,vertices=vertices,hideFlags=HideFlags.HideAndDontSave};
                tint=new Material(Shader.Find("Hidden/Internal-Colored")) {hideFlags=HideFlags.HideAndDontSave};
                tint.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha); tint.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);
                tint.SetInt("_Cull",(int)CullMode.Off); tint.SetInt("_ZWrite",0);
                cachedSource=session.mesh; cachedRenderer=source; cachedRevision=session.revision; bakedPose=PoseKey();
                RebuildOverlay(); error=null;
            }
            catch(Exception ex) { Cleanup(); error=ex.Message; }
        }
        List<float> bakedPose;
        // Everything BakeMesh depends on: renderer transform, bone matrices and shape-key weights.
        List<float> PoseKey()
        {
            var key=new List<float>();
            var source=session ? session.options.source : null;
            if(!source) return key;
            Action<Matrix4x4> add=m=>{for(int i=0;i<16;i++) key.Add(m[i]);};
            add(source.transform.localToWorldMatrix);
            if(source is SkinnedMeshRenderer skin)
            {
                foreach(var bone in skin.bones) add(bone ? bone.localToWorldMatrix : Matrix4x4.zero);
                int count=skin.sharedMesh ? skin.sharedMesh.blendShapeCount : 0;
                for(int i=0;i<count;i++) key.Add(skin.GetBlendShapeWeight(i));
            }
            return key;
        }
        bool PoseChanged() { var now=PoseKey(); return bakedPose==null || now.Count!=bakedPose.Count || !now.SequenceEqual(bakedPose); }
        void RebuildOverlay()
        {
            if (!session || !overlay) return;
            session.RebuildSelected();
            var indices=new List<int>();
            foreach(int id in session.selected) if(slot<0 || session.topology[id].submesh==slot)
            { var f=session.topology[id]; indices.Add(f.a); indices.Add(f.b); indices.Add(f.c); }
            overlay.SetTriangles(indices,0); Repaint();
        }
        void FrameSource() { if(mesh) { rotation=Quaternion.Euler(0,180,0); Frame(mesh.bounds,true); } }
        void All(bool clear=false,bool invert=false)
        {
            session.RefreshSource(); SelectionUpdated();
            var ids=new List<int>();
            for(int i=0;i<session.topology.Length;i++)
            {
                bool visible=slot<0||session.topology[i].submesh==slot;
                if (!clear && (invert ? (visible ? !session.selected.Contains(i) : session.selected.Contains(i)) : visible)) ids.Add(i);
            }
            session.ApplySelection(ids,false,false);
        }
        int Pick(Vector2 p)
        {
            if(!Hit(HandleUtility.GUIPointToWorldRay(p),out var hit)) return -1;
            int id=hit.triangleIndex;
            return id>=0 && id<slots.Length && (slot<0||slots[id]==slot) ? id : -1;
        }
        bool Hit(Ray ray,out RaycastHit hit)
        {
            hit=default(RaycastHit); if(!picker) return false;
            bool previous=Physics.queriesHitBackfaces;
            try { Physics.queriesHitBackfaces=true; return picker.Raycast(ray,out hit,float.PositiveInfinity); }
            finally { Physics.queriesHitBackfaces=previous; }
        }
        void PickFace(Vector2 p,bool island,bool add,bool remove)
        {
            int id=Pick(p);
            if(id<0) { if(!add&&!remove) session.ApplySelection(new int[0],false,false); return; }
            session.ApplySelection(island ? session.Island(id,false,slot) : new[]{id},add,remove);
        }
        void Project()
        {
            for(int i=0;i<centers.Length;i++)
            {
                Vector2 gui=HandleUtility.WorldToGUIPoint(centers[i]);
                projected[i]=new Vector3(gui.x,gui.y,Vector3.Dot(centers[i]-camera.transform.position,camera.transform.forward)-camera.nearClipPlane);
            }
        }
        void ApplyArea(Vector2 from,Vector2 to,bool brush)
        {
            // The camera cannot move during a stroke, so screen positions are projected once per stroke.
            if(!dragging || !projectedValid) { Project(); projectedValid=dragging; }
            var rect=Rect.MinMaxRect(Mathf.Min(from.x,to.x),Mathf.Min(from.y,to.y),Mathf.Max(from.x,to.x),Mathf.Max(from.y,to.y));
            int[] candidates=brush ? ScreenSelection.Brush(projected,slots,from,to,radius,slot) : ScreenSelection.Box(projected,slots,rect,slot);
            var ids=new List<int>();
            foreach(int id in candidates)
            {
                if(through) { ids.Add(id); continue; }
                var ray=HandleUtility.GUIPointToWorldRay(projected[id]);
                // Visible when the first hit is this face; the distance fallback covers coplanar duplicates.
                if(Hit(ray,out var hit) && (hit.triangleIndex==id || Vector3.Distance(hit.point,centers[id])<Mathf.Max(hit.distance*0.0001f,0.000001f))) ids.Add(id);
            }
            if(dragging) session.ApplyStrokeSelection(ids,brush||adding,removing); else session.ApplySelection(ids,brush||adding,removing);
        }
        void BeginStroke(Event e,int control)
        {
            session.RefreshSource();
            // Re-bake only when the mesh, selection source or pose moved; cooking the picker collider on every click is slow on large outfits.
            if(!mesh || PoseChanged()) Rebuild(); else SelectionUpdated();
            if(!mesh) return;
            Undo.IncrementCurrentGroup(); undoGroup=Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(ApmLocalization.T("메쉬 영역 선택"));
            beforeStroke=session.options.faces.ToArray();
            dragging=true; projectedValid=false; start=last=e.mousePosition; adding=e.shift; removing=e.control||e.command;
            GUIUtility.hotControl=control;
            if(mode==3)
            {
                ApplyArea(start,start,true);
            }
        }
        void FinishStroke()
        {
            if(!dragging) return;
            dragging=false; projectedValid=false; GUIUtility.hotControl=0;
            if(session && beforeStroke!=null) session.CommitStroke(beforeStroke);
            Undo.FlushUndoRecordObjects();
            if(undoGroup>=0) Undo.CollapseUndoOperations(undoGroup);
            undoGroup=-1; beforeStroke=null;
        }
        void OnLostFocus() { FinishStroke(); }
        bool hidTools;
        void RestoreTools() { if(hidTools) { Tools.hidden=false; hidTools=false; } }
        void ProtectOriginal(SceneView view)
        {
            if(view!=this) return;
            var e=Event.current;
            // Move/rotate gizmos of the globally selected original would draw here and drag the real avatar.
            if(!Tools.hidden) { Tools.hidden=true; hidTools=true; }
            // Selection is global in Unity: never let an isolated selection viewport delete
            // or duplicate whatever the user had selected in the original avatar hierarchy.
            if((e.type==EventType.ValidateCommand||e.type==EventType.ExecuteCommand) &&
                (e.commandName=="Delete"||e.commandName=="SoftDelete"||e.commandName=="Duplicate"||e.commandName=="Cut"||e.commandName=="Paste"||e.commandName=="SelectAll"))
            {
                if(e.commandName=="SelectAll" && e.type==EventType.ExecuteCommand && session) All();
                e.Use();
            }
            if(e.type==EventType.KeyDown && (e.keyCode==KeyCode.Delete||e.keyCode==KeyCode.Backspace)) e.Use();
            if(e.type==EventType.DragUpdated||e.type==EventType.DragPerform) {DragAndDrop.visualMode=DragAndDropVisualMode.Rejected;e.Use();}
        }
        void DrawEditor(SceneView view)
        {
            if(view!=this) return;
            RestoreTools();
            var e=Event.current;
            // Other tools target the last active SceneView; keep that the user's real Scene view.
            if(e.type==EventType.Layout && LastActive!=null && previousActive && lastActiveSceneView==this) LastActive.SetValue(null,previousActive);
            titleContent=new GUIContent(ApmLocalization.T("악세사리 분리 툴 · 메쉬 편집"));
            Rect panel=new Rect(48,8,Mathf.Max(650,position.width-100),145);
            Handles.BeginGUI();
            GUILayout.BeginArea(panel,EditorStyles.helpBox);
            if(!session || !session.options.source || !session.mesh)
                GUILayout.Label(ApmLocalization.T("설정 창에서 원본 메쉬를 지정하고 ‘메쉬 편집’을 누르세요."));
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(session.options.name,EditorStyles.boldLabel);
                GUILayout.FlexibleSpace(); GUILayout.Label(session.selected.Count+" / "+session.topology.Length+ApmLocalization.T(" 면"));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                mode=GUILayout.Toolbar(mode,new[]{ApmLocalization.T("아일랜드 · L"),ApmLocalization.T("면"),ApmLocalization.T("박스 · B"),ApmLocalization.T("브러시 · C")},GUILayout.Width(340));
                if(GUILayout.Button(ApmLocalization.T("전체 · A"),GUILayout.Width(72))) All();
                if(GUILayout.Button(ApmLocalization.T("해제"),GUILayout.Width(58))) All(true);
                if(GUILayout.Button(ApmLocalization.T("반전"),GUILayout.Width(58))) All(false,true);
                if(GUILayout.Button(ApmLocalization.T("맞춤 · F"),GUILayout.Width(85))) FrameSource();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                through=GUILayout.Toggle(through,ApmLocalization.T("관통 선택"),GUILayout.Width(100));
                bool nextWire=GUILayout.Toggle(wire,ApmLocalization.T("와이어"),GUILayout.Width(65));
                if(nextWire!=wire) { wire=nextWire; cameraMode=GetBuiltinCameraMode(wire?DrawCameraMode.TexturedWire:DrawCameraMode.Textured); }
                if(mode==3) { GUILayout.Label(ApmLocalization.T("크기"),GUILayout.Width(42)); radius=GUILayout.HorizontalSlider(radius,3,100,GUILayout.Width(100)); GUILayout.Label(Mathf.RoundToInt(radius)+" px",GUILayout.Width(45)); }
                GUILayout.FlexibleSpace();
                var labels=new string[session.mesh.subMeshCount+1]; labels[0]=ApmLocalization.T("전체 머티리얼");
                for(int i=1;i<labels.Length;i++) labels[i]=ApmLocalization.T("슬롯 ")+(i-1);
                int next=EditorGUILayout.Popup(slot+1,labels,GUILayout.Width(125))-1;
                if(next!=slot) { slot=next; RebuildOverlay(); }
                GUILayout.EndHorizontal();
                GUILayout.Label(mode==3 ? ApmLocalization.T("브러시: 여러 획 누적 선택 · Ctrl 제외 · Alt+드래그 회전 · 휠 확대 · Ctrl+Z 한 획 취소") : ApmLocalization.T("Shift 추가 · Ctrl 제외 · Alt+드래그 회전 · 휠 확대 · Ctrl+Z 되돌리기"),EditorStyles.miniLabel);
                GUILayout.Label(ApmLocalization.T("박스/브러시: 면 중심 기준  |  L: 커서 아래 연결 조각  |  Alt+A: 해제  |  Esc: 선택 취소"),EditorStyles.miniLabel);
            }
            if(!string.IsNullOrEmpty(error)) GUILayout.Label(error,EditorStyles.wordWrappedLabel);
            GUILayout.EndArea();
            Handles.EndGUI();
            if(!mesh || !session || EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) return;
            if(e.type==EventType.Repaint && tint && overlay)
            {
                tint.SetColor("_Color",new Color(1,.45f,.05f,.42f)); tint.SetInt("_ZTest",(int)(through?CompareFunction.Always:CompareFunction.LessEqual));
                tint.SetPass(0); Graphics.DrawMeshNow(overlay,Matrix4x4.identity);
            }
            bool inside=!panel.Contains(e.mousePosition) && e.mousePosition.y>0;
            if(!inside&&!dragging) return;
            int control=GUIUtility.GetControlID("APFM_Select".GetHashCode(),FocusType.Passive);
            if(e.type==EventType.Layout && !e.alt) HandleUtility.AddDefaultControl(control);
            if(e.type==EventType.MouseMove) { cursor=e.mousePosition; Repaint(); }
            if(e.type==EventType.KeyDown && !EditorGUIUtility.editingTextField && !e.control&&!e.command)
            {
                if(e.keyCode==KeyCode.Escape && dragging) { session.ApplyStrokeSelection(beforeStroke,false,false); FinishStroke(); }
                else if(e.keyCode==KeyCode.A) All(e.alt);
                else if(e.keyCode==KeyCode.B) mode=2;
                else if(e.keyCode==KeyCode.C) mode=3;
                else if(e.keyCode==KeyCode.L) PickFace(e.mousePosition,true,true,e.shift);
                else if(e.keyCode==KeyCode.F) FrameSource();
                else return;
                e.Use(); Repaint();
            }
            if(!e.alt && e.type==EventType.MouseDown && e.button==0) { BeginStroke(e,control); e.Use(); }
            else if(dragging && e.type==EventType.MouseDrag && e.button==0)
            {
                if(mode==3) ApplyArea(last,e.mousePosition,true);
                last=cursor=e.mousePosition; e.Use(); Repaint();
            }
            else if(dragging && e.type==EventType.MouseUp && e.button==0)
            {
                if(mode==3) ApplyArea(last,e.mousePosition,true);
                else if(Vector2.Distance(start,e.mousePosition)>5) ApplyArea(start,e.mousePosition,false);
                else PickFace(e.mousePosition,mode==0,adding,removing);
                FinishStroke(); e.Use(); Repaint();
            }
            if(e.type==EventType.Repaint)
            {
                Handles.BeginGUI();
                if(mode==3 && inside) { Handles.color=new Color(1,.65f,.2f); Handles.DrawWireDisc(cursor,Vector3.forward,radius); }
                else if(dragging) EditorGUI.DrawRect(Rect.MinMaxRect(Mathf.Min(start.x,last.x),Mathf.Min(start.y,last.y),Mathf.Max(start.x,last.x),Mathf.Max(start.y,last.y)),new Color(.2f,.6f,1,.25f));
                Handles.EndGUI();
            }
        }
    }
}
