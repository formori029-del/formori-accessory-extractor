using System;
using System.Collections.Generic;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public enum ExtractionMode { Rigid, Skinned }
    // FollowSource: saved relative to the original's bone/position, so it sits where the original is.
    // Ground: original position ignored; centred on the ground (avatar feet, or world origin without an avatar).
    public enum PlacementMode { FollowSource, Ground }

    public static class ApmVersion { public const string Current = "1.0.2"; }

    [Serializable]
    public sealed class ApmOptions
    {
        public Renderer source;
        public Transform avatarRoot;
        public Transform boneRoot;
        public Transform attachTarget;
        public ExtractionMode mode;
        public bool automaticBones;
        public List<int> faces = new List<int>();
        public string name = "Accessory";
        public bool useCustomName;
        public string customName = "";
        public bool useMA;
        public bool excludeExternalColliders;
        public Vector3 positionOffset;
        public Vector3 rotationOffset;
        public Vector3 scale = Vector3.one;
        public PlacementMode placement = PlacementMode.FollowSource;
        // MA attach: a mesh weighted 100% to one bone is saved as one object (Bone Proxy + renderer),
        // so toggling that object (One-click Inventory, MA toggles) always reaches the mesh.
        public bool singleObject = true;
        // Set by automatic tracing when the mesh is weighted to several body joints:
        // the copied armature is merged back into the avatar by MA Merge Armature.
        [NonSerialized] public bool bodyRig;
        [NonSerialized] public HashSet<Transform> bodySlots;
        // Scene references must survive (JSON round-trips drop them for plain classes).
        public ApmOptions Copy() { var copy = (ApmOptions)MemberwiseClone(); copy.faces = new List<int>(faces); copy.bodySlots = null; return copy; }
    }

    [Serializable]
    public sealed class ValidationReport
    {
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<string> details = new List<string>();
        public bool IsValid { get { return errors.Count == 0; } }
    }

    public sealed class MeshSlice : IDisposable
    {
        public Mesh mesh;
        public int[] sourceVertices;
        public int[] sourceSubmeshes;
        public void Dispose() { if (mesh != null && !UnityEditor.EditorUtility.IsPersistent(mesh)) UnityEngine.Object.DestroyImmediate(mesh); }
    }
}
