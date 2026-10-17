using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public sealed class ApmSession : ScriptableObject
    {
        public ApmOptions options = new ApmOptions { automaticBones = true };
        [SerializeField] string fingerprint = "";
        [NonSerialized] public Mesh mesh;
        [NonSerialized] public Face[] topology = new Face[0];
        [NonSerialized] public Vector3[] vertices = new Vector3[0];
        [NonSerialized] public Vector2[] uv = new Vector2[0];
        [NonSerialized] public HashSet<int> selected = new HashSet<int>();
        [NonSerialized] public Action changed;
        [NonSerialized] public int revision;
        [NonSerialized] string quickKey;
        [NonSerialized] string quickFingerprint;

        public void SetSource(Renderer renderer)
        {
            if (options.source == renderer && mesh != null) { RefreshSource(); return; }
            Undo.RecordObject(this, ApmLocalization.T("원본 Renderer 변경"));
            options.source = renderer;
            options.boneRoot = null; options.attachTarget = null;
            options.mode = RigAnalysis.DefaultMode(renderer);
            // A renderer from another avatar must not keep the previous avatar's root.
            if (renderer && options.avatarRoot && !RigAnalysis.Within(renderer.transform, options.avatarRoot)) options.avatarRoot = null;
            if (renderer && !options.avatarRoot)
            {
                foreach (var component in renderer.GetComponentsInParent<Component>(true))
                    if (component && component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")
                    { options.avatarRoot = component.transform; break; }
            }
            fingerprint = null;
            options.faces.Clear();
            RefreshSource();
            Notify();
        }

        // Polling uses a cheap key; the full content hash runs only when that key changes
        // or when full is requested (before validate/preview/save).
        public bool RefreshSource() { return Refresh(false); }
        public bool RefreshSourceFull() { return Refresh(true); }
        bool Refresh(bool full)
        {
            UpdateName();
            var current = options.source ? MeshSelection.GetMesh(options.source) : null;
            string next;
            try
            {
                string key = current ? QuickKey(current) : "";
                if (!full && key == quickKey && quickFingerprint != null) next = quickFingerprint;
                else { next = current ? MeshSelection.Fingerprint(current) : ""; quickKey = key; quickFingerprint = next; }
            }
            catch { ClearInvalidCache(); throw; }
            bool stale = fingerprint != next;
            bool rebuild = stale || mesh != current;
            if (rebuild)
            {
                if (stale) options.faces.Clear();
                try
                {
                    var nextTopology = current ? MeshSelection.Faces(current) : new Face[0];
                    var nextVertices = current ? current.vertices : new Vector3[0];
                    var nextUV = current ? current.uv : new Vector2[0];
                    topology = nextTopology; vertices = nextVertices; uv = nextUV;
                    fingerprint = next;
                    mesh = current;
                    revision++;
                }
                catch
                {
                    ClearInvalidCache();
                    throw;
                }
                Notify();
            }
            RebuildSelected();
            return stale;
        }

        static string QuickKey(Mesh mesh)
        {
            var key = new System.Text.StringBuilder();
            key.Append(mesh.GetInstanceID()).Append('|').Append(EditorUtility.GetDirtyCount(mesh)).Append('|').Append(mesh.isReadable)
               .Append('|').Append(mesh.vertexCount).Append('|').Append(mesh.blendShapeCount).Append('|').Append(mesh.bounds);
            for (int s = 0; s < mesh.subMeshCount; s++) key.Append('|').Append(mesh.GetSubMesh(s).indexCount);
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path)) key.Append('|').Append(AssetDatabase.GetAssetDependencyHash(path));
            return key.ToString();
        }

        public void UpdateName()
        {
            options.name = options.useCustomName ? options.customName : AutomaticName(options.source, options.avatarRoot);
        }
        public void SelectWholeMesh()
        {
            Undo.RecordObject(this,ApmLocalization.T("메쉬 전체 선택"));
            options.mode=RigAnalysis.DefaultMode(options.source);
            if(options.mode==ExtractionMode.Rigid) options.useMA=false;
            options.automaticBones=true;
            options.boneRoot=null; options.attachTarget=null;
            // One-click path: body colliders outside the copy cannot travel with the prefab; drop them with a warning.
            options.excludeExternalColliders=true;
            options.positionOffset=Vector3.zero; options.rotationOffset=Vector3.zero; options.scale=Vector3.one;
            var faces=new List<int>(topology.Length);
            for(int i=0;i<topology.Length;i++) faces.Add(i);
            ApplySelection(faces,false,false);
        }

        public static string AutomaticName(Renderer source, Transform avatarRoot)
        {
            if (!source) return "Accessory";
            Transform garment = source.transform;
            var prefab = PrefabUtility.GetOutermostPrefabInstanceRoot(source.gameObject);
            if (prefab && prefab.transform != avatarRoot) garment = prefab.transform;
            else if (avatarRoot && garment.IsChildOf(avatarRoot))
            {
                // The outfit is the ancestor owning an MA Merge Armature; organizer folders such as "Closet" are skipped.
                var outfit = OutfitRoot(source.transform, avatarRoot);
                if (outfit) garment = outfit;
                else while (garment.parent && garment.parent != avatarRoot) garment = garment.parent;
            }
            string name = garment.name.Replace("(Clone)", "").Trim();
            foreach (char c in "<>:\"/\\|?*") name = name.Replace(c, '_');
            var clean = new System.Text.StringBuilder();
            foreach (char c in name) clean.Append(char.IsControl(c) ? '_' : c);
            name = clean.ToString().Trim(' ', '.');
            return (string.IsNullOrEmpty(name) ? "Accessory" : name) + "_Accessory";
        }

        static Transform OutfitRoot(Transform source, Transform avatarRoot)
        {
            for (var t = source.parent; t && t != avatarRoot; t = t.parent)
                foreach (Transform child in t)
                    foreach (var c in child.GetComponents<Component>())
                        if (c && c.GetType().FullName == RigAnalysis.MergeArmature) return t;
            return null;
        }

        public void ApplySelection(IEnumerable<int> faces, bool add, bool remove) { Apply(faces, add, remove, true); }
        // Inside a brush stroke: the stroke recorded once at its start, so per-event snapshots
        // of the whole face list are not needed for its single undo step.
        public void ApplyStrokeSelection(IEnumerable<int> faces, bool add, bool remove) { Apply(faces, add, remove, false); }
        // One clean undo step for a whole stroke: snapshot the pre-stroke faces, then write the result.
        public void CommitStroke(int[] before)
        {
            var after = new List<int>(options.faces);
            options.faces = new List<int>(before);
            Undo.RecordObject(this, ApmLocalization.T("악세사리 면 선택"));
            options.faces = after;
            Notify();
        }
        void Apply(IEnumerable<int> faces, bool add, bool remove, bool record)
        {
            if (record) Undo.RecordObject(this, ApmLocalization.T("악세사리 면 선택"));
            RebuildSelected();
            if (!add && !remove) selected.Clear();
            foreach (int face in faces)
                if (face >= 0 && face < topology.Length)
                {
                    if (remove) selected.Remove(face); else selected.Add(face);
                }
            options.faces = new List<int>(selected);
            options.faces.Sort();
            Notify();
        }

        public void Restore(ApmOptions restored)
        {
            Undo.RecordObject(this, ApmLocalization.T("악세사리 레시피 불러오기"));
            var faces = new List<int>(restored.faces);
            options = restored;
            fingerprint = null;
            RefreshSource();
            options.faces = faces;
            RebuildSelected();
            Notify();
        }

        [NonSerialized] int[] islandLabels;
        [NonSerialized] string islandKey;
        // Faces connected to face (same island), cached per source revision, UV/mesh mode and slot filter.
        public int[] Island(int face, bool byUV, int slot)
        {
            if (face < 0 || face >= topology.Length) return new int[0];
            var labels = IslandLabels(byUV, slot);
            int label = labels[face]; if (label < 0) return new int[0];
            var result = new List<int>();
            for (int i = 0; i < labels.Length; i++) if (labels[i] == label) result.Add(i);
            return result.ToArray();
        }
        public int[] IslandLabels(bool byUV, int slot)
        {
            string key = revision + "|" + byUV + "|" + slot;
            if (islandKey != key || islandLabels == null) { islandLabels = MeshSelection.IslandLabels(topology, vertices, uv, byUV, slot); islandKey = key; }
            return islandLabels;
        }

        public string FingerprintValue { get { return fingerprint; } }
        // After a domain reload or a mode switch: put back saved settings and faces only if the mesh is unchanged.
        public bool RestoreState(ApmOptions saved, string savedFingerprint, bool record)
        {
            if (saved == null) return false;
            if (record) Undo.RecordObject(this, ApmLocalization.T("악세사리 설정 변경"));
            var faces = new List<int>(saved.faces);
            options = saved; fingerprint = null; quickKey = null; quickFingerprint = null;
            RefreshSourceFull();
            bool same = !string.IsNullOrEmpty(savedFingerprint) && fingerprint == savedFingerprint;
            options.faces = same ? faces : new List<int>();
            Notify();
            return same;
        }
        public static ApmOptions Clone(ApmOptions source)
        {
            return source == null ? null : source.Copy();
        }

        public void RebuildSelected()
        {
            if (selected == null) selected = new HashSet<int>();
            selected.Clear();
            foreach (int id in options.faces) if (id >= 0 && id < topology.Length) selected.Add(id);
        }
        void ClearInvalidCache()
        {
            mesh = null; fingerprint = null; quickKey = null; quickFingerprint = null;
            topology = new Face[0]; vertices = new Vector3[0]; uv = new Vector2[0];
            options.faces.Clear(); Notify();
        }
        void Notify() { RebuildSelected(); if (changed != null) changed(); }
    }
}
