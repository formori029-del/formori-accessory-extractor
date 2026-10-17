using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AccessoryPrefabMaker
{
    [Serializable]
    public sealed class BatchItem
    {
        public Renderer source;
        public string sourceName;
        public string path;
        public string error;
        public int warnings;
        public bool Succeeded { get { return !string.IsNullOrEmpty(path); } }
    }

    // Several selected objects -> one accessory prefab per mesh, each with the simple-mode defaults.
    public static class BatchExtractor
    {
        public static List<Renderer> CollectRenderers(IEnumerable<Object> objects)
        {
            var result = new List<Renderer>();
            foreach (var value in objects ?? new Object[0])
            {
                IEnumerable<Renderer> found = value is Renderer r ? new[] { r } : value is GameObject go ? go.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
                foreach (var renderer in found)
                    if ((renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && !EditorUtility.IsPersistent(renderer) && MeshSelection.GetMesh(renderer) && !result.Contains(renderer))
                        result.Add(renderer);
            }
            return result;
        }

        public static Transform AvatarRootOf(Renderer source)
        {
            if (!source) return null;
            foreach (var component in source.GetComponentsInParent<Component>(true))
                if (component && component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") return component.transform;
            return null;
        }

        // Same choices as simple mode: whole mesh, automatic bones, body colliders outside the copy dropped.
        public static ApmOptions SimpleOptions(Renderer source)
        {
            var options = new ApmOptions { source = source, automaticBones = true, excludeExternalColliders = true, mode = RigAnalysis.DefaultMode(source), useCustomName = true, customName = source.name, name = source.name, avatarRoot = AvatarRootOf(source) };
            var mesh = MeshSelection.GetMesh(source);
            if (mesh && mesh.isReadable) options.faces = Enumerable.Range(0, MeshSelection.Faces(mesh).Length).ToList();
            return options;
        }

        // One failure never stops the rest; every source gets a path or a reason.
        // place: also put each result in the scene where "add to scene" would (one undo group for the batch).
        public static List<BatchItem> ExtractEach(IList<Renderer> sources, string folder, Action<int, int, string> progress = null, bool place = false, PlacementMode placement = PlacementMode.FollowSource)
        {
            var items = new List<BatchItem>();
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                var item = new BatchItem { source = source, sourceName = source ? source.name : "?" };
                items.Add(item);
                if (progress != null) progress(i, sources.Count, item.sourceName);
                try
                {
                    if (!source) throw new ArgumentException(ApmLocalization.T("원본 Renderer를 지정하세요."));
                    var options = SimpleOptions(source);
                    options.placement = placement;
                    var report = PrefabGenerator.Validate(options);
                    item.warnings = report.warnings.Count;
                    if (!report.IsValid) { item.error = string.Join("\n", report.errors); continue; }
                    item.path = PrefabGenerator.Save(options, folder);
                    if (place) ApmWindow.InstantiateResult(AssetDatabase.LoadAssetAtPath<GameObject>(item.path), options);
                }
                catch (Exception e) { item.error = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException.Message : e.Message; }
            }
            Undo.CollapseUndoOperations(group);
            return items;
        }

        // Check every mesh without saving anything (the window's "check" button in object mode).
        public static List<BatchItem> ValidateEach(IList<Renderer> sources)
        {
            var items = new List<BatchItem>();
            foreach (var source in sources)
            {
                var item = new BatchItem { source = source, sourceName = source ? source.name : "?" };
                items.Add(item);
                try
                {
                    var report = PrefabGenerator.Validate(SimpleOptions(source));
                    item.warnings = report.warnings.Count;
                    if (!report.IsValid) item.error = string.Join("\n", report.errors);
                }
                catch (Exception e) { item.error = e.Message; }
            }
            return items;
        }
    }
}
