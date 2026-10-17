using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AccessoryPrefabMaker
{
    public static class RigAnalysis
    {
        // Derive from selection each time; never use stale roots from an earlier selection.
        public static void ResolveAutomatic(ApmOptions o)
        {
            if(o==null || !o.automaticBones || o.mode!=ExtractionMode.Skinned) return;
            o.boneRoot=null;o.attachTarget=null;o.bodyRig=false;o.bodySlots=null;
            var bones=RequiredBones(o);
            if(bones.Length==0) throw new InvalidOperationException(ApmLocalization.T("선택한 면에 본 웨이트가 없습니다. 면을 선택하거나 고정 모드를 사용하세요."));
            var root=bones[0];while(root && bones.Any(b=>!Within(b,root))) root=root.parent;
            if(!root || (o.avatarRoot && !Within(root,o.avatarRoot))) throw new InvalidOperationException(ApmLocalization.T("선택 본의 공통 계층이 아바타 안에 없습니다."));
            var human=BodyBones(o);
            // A single humanoid attachment follows the original body bone through Content.
            // Several body joints cannot be frozen into a copy: merge the copied armature back instead.
            if(bones.Any(b=>human.Contains(b) && b!=root)) { ResolveBodyRig(o,bones,human);return; }
            if(!human.Contains(root))
                for(var ancestor=root.parent;ancestor && ancestor!=o.avatarRoot && !human.Contains(ancestor);ancestor=ancestor.parent)
                    if(ancestor.GetComponents<Component>().Any(c=>c && c.GetType().FullName==PhysBone && Controls(c,root))) root=ancestor;
            foreach(var pb in PhysicsCandidates(o))
            {
                var controlled=ControlledRoot(pb);
                if(!human.Contains(root) && Within(root,controlled) && Controls(pb,root)) root=controlled;
            }
            var attach=human.Contains(root) ? root:root.parent;
            if(!attach || root==o.avatarRoot) throw new InvalidOperationException(ApmLocalization.T("아바타 전체가 아닌 악세사리 본 가지를 선택하세요. 필요한 경우 자동 추적을 끄고 본을 지정하세요."));
            o.boneRoot=root;o.attachTarget=attach;
            try {ValidateHumanoidPaths(o,human);}
            catch(InvalidOperationException) {o.boneRoot=null;o.attachTarget=null;ResolveBodyRig(o,bones,human);return;}
            // A constraint driven by another body joint (e.g. a jacket panel following Upper_arm) only keeps
            // working if that joint is in the copy, i.e. when the armature is merged back.
            if(ReferencesOtherBodyJoint(o,human)) {o.boneRoot=null;o.attachTarget=null;ResolveBodyRig(o,bones,human);}
        }
        static bool ReferencesOtherBodyJoint(ApmOptions o,HashSet<Transform> human)
        {
            if(human.Count==0) return false;
            foreach(var c in CopyComponents(o,CopyTransforms(o)))
                foreach(var reference in References(c))
                {
                    // Body colliders are handled by the collider exclusion, not by merging the armature.
                    if(reference.Value is Component referenced && referenced && referenced.GetType().FullName==Collider) continue;
                    var t=TransformOf(reference.Value);
                    if(t && human.Contains(t) && t!=o.attachTarget && !Within(t,o.boneRoot)) return true;
                }
            return false;
        }
        // Copy the armature from its root (outfit Armature with MA Merge Armature, or the avatar's own
        // Armature) and let MA merge it into the avatar body at build time, exactly like the source outfit.
        static void ResolveBodyRig(ApmOptions o,Transform[] bones,HashSet<Transform> human)
        {
            if(FindType(MergeArmature)==null) throw new InvalidOperationException(ApmLocalization.T("여러 몸 관절에 웨이트된 메쉬는 Modular Avatar가 있어야 아바타 몸에 합칠 수 있습니다. MA를 설치하거나 한 부착점의 부분만 선택하세요."));
            if(!o.avatarRoot) throw new InvalidOperationException(ApmLocalization.T("여러 몸 관절에 웨이트된 메쉬는 아바타 루트가 필요합니다."));
            var roots=bones.Select(b=>ArmatureRoot(o,b)).Distinct().ToArray();
            if(roots.Length!=1 || !roots[0]) throw new InvalidOperationException(ApmLocalization.T("서로 다른 아마추어(의상·아바타)에 함께 웨이트된 메쉬는 지원하지 않습니다."));
            o.boneRoot=roots[0];o.attachTarget=o.avatarRoot;o.bodyRig=true;o.bodySlots=human;
        }
        // The transform MA merges: an MA Merge Armature host, or the avatar's Armature (parent of Hips).
        internal static Transform ArmatureRoot(ApmOptions o,Transform bone)
        {
            var merges=new HashSet<Transform>(MergedArmatures(o.avatarRoot).Select(m=>m.Key));
            var animator=o.avatarRoot.GetComponent<Animator>();
            var hips=animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips):null;
            var avatarArmature=hips && hips.parent!=o.avatarRoot ? hips.parent:null;
            for(var t=bone;t && t!=o.avatarRoot;t=t.parent) if(merges.Contains(t) || t==avatarArmature) return t;
            return null;
        }
        // Humanoid bones of the avatar plus outfit bones that Modular Avatar merges into them.
        // An outfit's own Chest/Upper_arm is a body joint after the build, not an accessory bone.
        internal static HashSet<Transform> BodyBones(ApmOptions o)
        {
            var human=new HashSet<Transform>();
            var animator=o.avatarRoot ? o.avatarRoot.GetComponent<Animator>():o.source.GetComponentInParent<Animator>();
            if(animator && animator.isHuman)
                foreach(HumanBodyBones id in Enum.GetValues(typeof(HumanBodyBones)))
                    if(id!=HumanBodyBones.LastBone) {var bone=animator.GetBoneTransform(id);if(bone) human.Add(bone);}
            var scope=o.avatarRoot ? o.avatarRoot:(animator ? animator.transform:null);
            if(scope) AddMergedBodyBones(scope,human);
            return human;
        }
        public const string MergeArmature = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        public static void AddMergedBodyBones(Transform avatarRoot,HashSet<Transform> human)
        {
            foreach(var merge in MergedArmatures(avatarRoot))
                foreach(var pair in MergedBones(merge.Key,merge.Value.Key,merge.Value.Value))
                    if(human.Contains(pair.Value)) human.Add(pair.Key);
        }
        // Outfit bone -> avatar bone, following MA's rule: children match by name after removing prefix/suffix.
        public static Dictionary<Transform,Transform> MergedBoneMap(Transform avatarRoot)
        {
            var map=new Dictionary<Transform,Transform>();
            if(avatarRoot) foreach(var merge in MergedArmatures(avatarRoot))
                foreach(var pair in MergedBones(merge.Key,merge.Value.Key,merge.Value.Value)) map[pair.Key]=pair.Value;
            return map;
        }
        static IEnumerable<KeyValuePair<Transform,KeyValuePair<Transform,string[]>>> MergedArmatures(Transform avatarRoot)
        {
            foreach(var c in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if(!c || c.GetType().FullName!=MergeArmature) continue;
                var so=new SerializedObject(c);
                var path=so.FindProperty("mergeTarget.referencePath");var targetObject=so.FindProperty("mergeTarget.targetObject");
                Transform target=null;
                if(path!=null && !string.IsNullOrEmpty(path.stringValue)) target=avatarRoot.Find(path.stringValue);
                if(!target && targetObject!=null) target=TransformOf(targetObject.objectReferenceValue);
                if(!target || target==c.transform) continue;
                var prefix=so.FindProperty("prefix");var suffix=so.FindProperty("suffix");
                yield return new KeyValuePair<Transform,KeyValuePair<Transform,string[]>>(c.transform,new KeyValuePair<Transform,string[]>(target,new[]{prefix!=null ? prefix.stringValue:"",suffix!=null ? suffix.stringValue:""}));
            }
        }
        static IEnumerable<KeyValuePair<Transform,Transform>> MergedBones(Transform outfit,Transform avatar,string[] affixes)
        {
            foreach(Transform child in outfit)
            {
                string name=child.name;
                if(affixes[0].Length>0 && name.StartsWith(affixes[0],StringComparison.Ordinal)) name=name.Substring(affixes[0].Length);
                if(affixes[1].Length>0 && name.EndsWith(affixes[1],StringComparison.Ordinal)) name=name.Substring(0,name.Length-affixes[1].Length);
                Transform match=null;
                foreach(Transform candidate in avatar) if(candidate.name==name) {match=candidate;break;}
                if(!match) continue;
                yield return new KeyValuePair<Transform,Transform>(child,match);
                foreach(var nested in MergedBones(child,match,affixes)) yield return nested;
            }
        }
        internal static void ValidateHumanoidPaths(ApmOptions o,HashSet<Transform> human)
        {
            if(human.Count>0 && CopyTransforms(o).Any(t=>human.Contains(t) && t!=o.attachTarget))
                throw new InvalidOperationException(ApmLocalization.T("필요한 부모/PhysBone 계층에 다른 몸 관절이 포함됩니다. 한 부착점의 악세사리만 선택하세요. 몸 관절 리타기팅은 지원하지 않습니다."));
        }
        // A rigid copy is frozen, so only physics/constraints that actually move this renderer matter;
        // colliders on Head/Chest above an earring do not.
        internal static bool MovesRigid(ApmOptions o)
        {
            var t=o.source.transform;
            if(t.GetComponents<Component>().Any(IsSupported)) return true;
            var scope=o.avatarRoot ? o.avatarRoot:t.root;
            return scope.GetComponentsInChildren<Component>(true).Any(c=>(IsConstraint(c) && Within(t,ConstraintTarget(c))) || (c && c.GetType().FullName==PhysBone && Controls(c,t)));
        }
        static bool Controls(Component pb,Transform target)
        {
            var serialized=new SerializedObject(pb);var controlled=ControlledRoot(pb);
            if(!Within(target,controlled)) return false;
            var ignores=serialized.FindProperty("ignoreTransforms");
            if(ignores!=null) for(int i=0;i<ignores.arraySize;i++)
            {var ignored=ignores.GetArrayElementAtIndex(i).objectReferenceValue as Transform;if(ignored!=controlled && Within(target,ignored)) return false;}
            return true;
        }
        static Transform ControlledRoot(Component pb)
        {
            var property=new SerializedObject(pb).FindProperty("rootTransform");var root=property!=null ? property.objectReferenceValue as Transform:null;
            return root ? root:pb.transform;
        }
        static Component[] PhysicsCandidates(ApmOptions o)
        {
            var root=o.avatarRoot ? o.avatarRoot:o.source.transform.root;
            return root.GetComponentsInChildren<Component>(true).Where(c=>c && c.GetType().FullName==PhysBone).ToArray();
        }
        internal static Component[] CopyComponents(ApmOptions o,HashSet<Transform> keep)
        {
            var result=keep.Where(t=>!IsBodyAttachment(o,t)).SelectMany(t=>t.GetComponents<Component>()).Where(IsSupported);
            if(o.automaticBones)
            {
                // A PhysBone hosted on a body bone may still drive accessory bones (e.g. breast bones under Chest);
                // what matters is that it never controls a body joint itself.
                result=result.Concat(PhysicsCandidates(o).Where(c=>!IsBodyAttachment(o,ControlledRoot(c)) && Within(ControlledRoot(c),o.boneRoot) && keep.Any(b=>!IsBodyAttachment(o,b) && Controls(c,b))));
            }
            var scope=o.avatarRoot ? o.avatarRoot:o.source.transform.root;
            var list=result.Concat(scope.GetComponentsInChildren<Component>(true).Where(c=>IsConstraint(c) && ((keep.Contains(ConstraintTarget(c)) && !IsBodyAttachment(o,ConstraintTarget(c))) || ConstraintTarget(c)==o.source.transform))).Distinct().ToList();
            // Colliders on a body bone that a copied PhysBone uses travel with it (they merge into the same body bone).
            foreach(var pb in list.Where(c=>c.GetType().FullName==PhysBone).ToArray())
                foreach(var reference in References(pb))
                    if(reference.Value is Component collider && collider && collider.GetType().FullName==Collider && keep.Contains(collider.transform) && IsBodyAttachment(o,collider.transform) && !list.Contains(collider)) list.Add(collider);
            return list.ToArray();
        }
        // When the accessory hangs directly off a body bone (Head, Chest...), that bone only marks
        // where to attach. Its own colliders, contacts and hair PhysBones belong to the avatar.
        // A SkinnedMeshRenderer without bone weights (shape keys only) is extracted as a rigid mesh.
        internal static ExtractionMode DefaultMode(Renderer renderer)
        {
            var skin=renderer as SkinnedMeshRenderer;
            if(!skin) return ExtractionMode.Rigid;
            var mesh=skin.sharedMesh;
            if(!mesh || !mesh.isReadable) return ExtractionMode.Skinned;
            using(var weights=mesh.GetAllBoneWeights()) return weights.Length>0 && skin.bones.Length>0 ? ExtractionMode.Skinned : ExtractionMode.Rigid;
        }
        internal static bool IsBodyAttachment(ApmOptions o,Transform t)
        { return o.automaticBones && o.boneRoot && ((o.boneRoot==o.attachTarget && t==o.boneRoot) || (o.bodyRig && o.bodySlots!=null && o.bodySlots.Contains(t))); }
        public static HashSet<Transform> CopyTransforms(ApmOptions o)
        {
            if(!o.boneRoot) return new HashSet<Transform>();
            if(!o.automaticBones) return new HashSet<Transform>(o.boneRoot.GetComponentsInChildren<Transform>(true));
            var keep=new HashSet<Transform>{o.boneRoot};
            Action<Transform> addPath=t=>{for(;t && Within(t,o.boneRoot);t=t.parent) keep.Add(t);};
            foreach(var bone in RequiredBones(o)) addPath(bone);
            // Physics needs unweighted end bones and referenced colliders/ignore transforms.
            // Expand to a fixed point so a referenced collider's own transform is also copied.
            int previous;
            do {
                previous=keep.Count;
                foreach(var c in CopyComponents(o,keep))
                {
                    if(c.GetType().FullName==PhysBone)
                    {
                        var controlled=ControlledRoot(c);
                        if(Within(controlled,o.boneRoot)) foreach(var child in controlled.GetComponentsInChildren<Transform>(true)) addPath(child);
                    }
                    foreach(var reference in References(c)) {var target=TransformOf(reference.Value);if(Within(target,o.boneRoot)) addPath(target);}
                }
            } while(previous!=keep.Count);
            return keep;
        }
        public static Transform[] RequiredBones(ApmOptions options)
        {
            var renderer=options==null ? null:options.source as SkinnedMeshRenderer;
            var mesh=renderer ? renderer.sharedMesh:null;
            if(!mesh || options.faces==null || options.faces.Count==0) return new Transform[0];
            var faces=MeshSelection.Faces(mesh);var vertices=new HashSet<int>();
            foreach(var index in options.faces)
            {
                if(index<0 || index>=faces.Length) throw new ArgumentException(ApmLocalization.T("선택 인덱스가 유효하지 않습니다."));
                var face=faces[index];vertices.Add(face.a);vertices.Add(face.b);vertices.Add(face.c);
            }
            var used=new SortedSet<int>();
            using(var counts=mesh.GetBonesPerVertex()) using(var weights=mesh.GetAllBoneWeights())
            {
                int cursor=0;
                for(int v=0;v<counts.Length;v++) for(int j=0;j<counts[v];j++) { var weight=weights[cursor++];if(vertices.Contains(v) && weight.weight>0) used.Add(weight.boneIndex); }
            }
            var bones=renderer.bones;
            if(used.Any(i=>i<0 || i>=bones.Length || !bones[i])) throw new InvalidOperationException(ApmLocalization.T("필수 웨이트 본 참조가 누락되었습니다."));
            return used.Select(i=>bones[i]).Distinct().ToArray();
        }
        public static Transform SuggestBoneRoot(ApmOptions options)
        {
            if(options==null || !options.attachTarget) return null;
            var bones=RequiredBones(options).Where(b=>b!=options.attachTarget).ToArray();
            if(bones.Length==0 || bones.Any(b=>!Within(b,options.attachTarget))) return null;
            var candidate=bones[0];while(candidate && bones.Any(b=>!Within(b,candidate))) candidate=candidate.parent;
            if(!candidate || candidate==options.attachTarget) return null;
            var usedBoneIndices=new Dictionary<Mesh,HashSet<int>>();
            if(ScopeComponents(options).OfType<SkinnedMeshRenderer>().Any(r=>r!=options.source && UsesRig(r,candidate,usedBoneIndices))) return null;
            return candidate;
        }
        static bool UsesRig(SkinnedMeshRenderer renderer,Transform root,Dictionary<Mesh,HashSet<int>> cache)
        {
            var bones=renderer.bones;
            if(!bones.Any(b=>Within(b,root))) return false;
            var mesh=renderer.sharedMesh;
            // An unreadable/missing mesh cannot prove that its listed rig is unused.
            if(!mesh || !mesh.isReadable) return true;
            if(!cache.TryGetValue(mesh,out var indices))
            {
                indices=new HashSet<int>();
                try { using(var weights=mesh.GetAllBoneWeights()) for(int i=0;i<weights.Length;i++) if(weights[i].weight>0) indices.Add(weights[i].boneIndex); }
                catch { return true; }
                cache[mesh]=indices;
            }
            return indices.Any(i=>i<0 || i>=bones.Length || Within(bones[i],root));
        }
        internal const string PhysBone = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        internal const string Collider = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        internal const string BoneProxy = "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy";
        internal static Type FindType(string name) { return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null); }
        static readonly HashSet<string> ConstraintNames=new HashSet<string>{"ParentConstraint","PositionConstraint","RotationConstraint","ScaleConstraint","AimConstraint","LookAtConstraint"};
        internal static bool IsConstraint(Component c)
        {
            if(!c) return false;var type=c.GetType();
            return (type.Namespace=="UnityEngine.Animations" && ConstraintNames.Contains(type.Name)) ||
                (type.Namespace=="VRC.SDK3.Dynamics.Constraint.Components" && type.Name.StartsWith("VRC",StringComparison.Ordinal) && ConstraintNames.Contains(type.Name.Substring(3)));
        }
        internal static Transform ConstraintTarget(Component c)
        {
            if(!IsConstraint(c)) return null;
            var target=new SerializedObject(c).FindProperty("TargetTransform");
            return target!=null && target.objectReferenceValue ? target.objectReferenceValue as Transform:c.transform;
        }
        internal static bool IsSupported(Component c) { return c && (c.GetType().FullName == PhysBone || c.GetType().FullName == Collider || IsConstraint(c)); }
        internal static bool Within(Transform t, Transform root) { return t && root && (t == root || t.IsChildOf(root)); }
        internal static Transform TransformOf(Object o) { return o is GameObject ? ((GameObject)o).transform : o is Component ? ((Component)o).transform : null; }
        internal static IEnumerable<Component> SceneComponents()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i); if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) foreach (var c in root.GetComponentsInChildren<Component>(true)) if (c) yield return c;
            }
        }
        // Only the avatar (and Animators above it) can drive its bones; other avatars in the scene cannot.
        // Scanning every loaded scene cost seconds per check in projects with many test avatars.
        internal static IEnumerable<Component> ScopeComponents(ApmOptions o)
        {
            if(!o.avatarRoot) { foreach(var c in SceneComponents()) yield return c; yield break; }
            for(var t=o.avatarRoot.parent;t;t=t.parent) foreach(var c in t.GetComponents<Component>()) if(c) yield return c;
            foreach(var c in o.avatarRoot.GetComponentsInChildren<Component>(true)) if(c) yield return c;
            if(o.source && !Within(o.source.transform,o.avatarRoot)) foreach(var c in SceneComponents()) if(!Within(c.transform,o.avatarRoot)) yield return c;
        }
        internal static void ValidateScale(Transform t, ValidationReport report)
        {
            for (; t; t = t.parent)
            {
                if (!Uniform(t.localScale)) { report.errors.Add(ApmLocalization.T("음수·비균일 스케일은 지원하지 않습니다: ") + t.name); return; }
            }
        }
        // Relative tolerance: Blender FBX imports carry float noise such as (1, 1, 1.000016),
        // which moves a 1 m bone by ~0.02 mm and must not block extraction.
        internal static bool Uniform(Vector3 s) { float tolerance = Mathf.Max(Mathf.Abs(s.x), 0.000001f) * .0001f; return Finite(s.x) && Finite(s.y) && Finite(s.z) && s.x > 0.000001f && Mathf.Abs(s.x - s.y) <= tolerance && Mathf.Abs(s.x - s.z) <= tolerance; }
        internal static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        internal static string Path(Transform root, Transform target)
        {
            if (!Within(target, root)) throw new InvalidOperationException(ApmLocalization.T("대상이 아바타 루트 밖에 있습니다."));
            if (target == root) return "";
            var parts = new List<string>();
            for (var t = target; t != root; t = t.parent)
            {
                if (t.name.Contains("/") || t.parent.Cast<Transform>().Count(x => x.name == t.name) != 1) throw new InvalidOperationException(ApmLocalization.T("중복 이름 또는 '/' 때문에 대상 경로가 모호합니다: ") + t.name);
                parts.Add(t.name);
            }
            parts.Reverse(); return string.Join("/", parts);
        }
        internal static void ValidateRig(ApmOptions o, Mesh mesh, ValidationReport report)
        {
            var source = o.source as SkinnedMeshRenderer;
            if (!source || !o.boneRoot || !o.attachTarget) { report.errors.Add(ApmLocalization.T("전용 본 모드는 SkinnedMeshRenderer, 전용 본 루트와 부착 대상이 필요합니다.")); return; }
            if (Within(o.attachTarget, o.boneRoot) && !(o.automaticBones && o.attachTarget==o.boneRoot)) report.errors.Add(ApmLocalization.T("부착 대상은 전용 본 가지 밖에 있어야 합니다."));
            var copied=CopyTransforms(o);
            foreach(var transform in copied) ValidateScale(transform, report);
            var vertices = new HashSet<int>(); var faces = MeshSelection.Faces(mesh);
            foreach (var id in o.faces) { if(id<0 || id>=faces.Length) continue; var f=faces[id];vertices.Add(f.a);vertices.Add(f.b);vertices.Add(f.c); }
            var counts=mesh.GetBonesPerVertex(); var weights=mesh.GetAllBoneWeights();
            try
            {
                if(counts.Length!=mesh.vertexCount || weights.Length==0) report.errors.Add(ApmLocalization.T("선택 메쉬에 유효한 본 웨이트가 없습니다."));
                int cursor=0; var needed=new HashSet<int>();
                for(int v=0;v<counts.Length;v++) for(int j=0;j<counts[v];j++) { var w=weights[cursor++]; if(vertices.Contains(v) && w.weight>0) needed.Add(w.boneIndex); }
                foreach(var index in needed)
                {
                    if(index<0 || index>=source.bones.Length || index>=mesh.bindposes.Length || !source.bones[index]) { report.errors.Add(ApmLocalization.T("누락된 웨이트 본/바인드포즈: ")+index); continue; }
                    if(!Within(source.bones[index],o.boneRoot) && source.bones[index]!=o.attachTarget) report.errors.Add(ApmLocalization.T("전용 본 가지 밖의 필수 웨이트 본: ")+source.bones[index].name);
                }
                report.details.Add(ApmLocalization.T("선택 웨이트 본: ")+needed.Count+ApmLocalization.T(", 복사 계층 Transform: ")+copied.Count+ApmLocalization.T(" · 원본 유지"));
                report.details.Add(ApmLocalization.T("부착 대상: ")+o.attachTarget.name);
                foreach(var bone in RequiredBones(o)) report.details.Add(ApmLocalization.T("필요 본: ")+(o.avatarRoot && Within(bone,o.avatarRoot) ? Path(o.avatarRoot,bone):bone.name));
            } finally { counts.Dispose();weights.Dispose(); }
            var supported=CopyComponents(o,copied);
            var dependencies=new HashSet<Transform>(copied.Concat(supported.Select(c=>c.transform)));
            var components=copied.Where(t=>!IsBodyAttachment(o,t)).SelectMany(t=>t.GetComponents<Component>()).Concat(supported).Distinct().ToArray();
            report.details.Add(ApmLocalization.T("복사 Constraint: ")+supported.Count(IsConstraint));
            foreach(var c in supported) ValidateScale(c.transform,report);
            foreach(var c in components)
            {
                if(!c) { report.errors.Add(ApmLocalization.T("전용 본 계층에 Missing Script가 있습니다."));continue; }
                if(c is Transform || c==o.source || (c is MeshFilter && c.gameObject==o.source.gameObject)) continue;
                if(o.bodyRig && c.transform==o.boneRoot && c.GetType().FullName==MergeArmature) continue;
                if(!IsSupported(c)) { report.errors.Add(ApmLocalization.T("지원하지 않는 본 컴포넌트: ")+c.GetType().Name+" ("+c.name+")");continue; }
                if(IsConstraint(c))
                {
                    var target=ConstraintTarget(c);
                    // Rest values are local to the target's parent, not world-space poses.
                    // A copied target must retain that exact parent frame.
                    if(!target || target==o.attachTarget || (target.parent!=o.attachTarget && !copied.Contains(target.parent)))
                        report.errors.Add(ApmLocalization.T("Constraint 대상의 부모 좌표계를 보존할 수 없습니다. 부모를 포함하도록 본 루트/부착 대상을 지정하세요: ")+c.name);
                }
                foreach(var reference in References(c))
                {
                    var target=reference.Value;
                    if(!target || EditorUtility.IsPersistent(target)) continue;
                    var transform=TransformOf(target);
                    bool allowed=transform && (copied.Contains(transform) || transform==o.attachTarget || transform==source.transform || supported.Any(s=>s.transform==transform));
                    if(allowed && target is Component && !(target is Transform) && !IsSupported((Component)target) && target!=o.source) allowed=false;
                    if(allowed) continue;
                    if(IsExcludedCollider(o,c,reference.Key,target)) report.warnings.Add(ApmLocalization.T("명시적으로 외부 Collider를 제외합니다: ")+target.name);
                    else report.errors.Add(ApmLocalization.T("외부 참조를 해결해야 합니다: ")+c.name+"."+reference.Key+" → "+target.name);
                }
                if(c.GetType().FullName==PhysBone)
                {
                    var p=new SerializedObject(c).FindProperty("rootTransform");var controlled=p!=null ? p.objectReferenceValue as Transform : null;
                    if(controlled && !Within(controlled,o.boneRoot)) report.errors.Add(ApmLocalization.T("공유/외부 루트를 제어하는 PhysBone은 지원하지 않습니다: ")+c.name);
                }
            }
            var usedBoneIndices=new Dictionary<Mesh,HashSet<int>>();
            foreach(var c in ScopeComponents(o))
            {
                // Other outfit meshes share the body armature by design when it is merged back.
                if(!o.bodyRig && c is SkinnedMeshRenderer && c!=source && UsesRig((SkinnedMeshRenderer)c,o.boneRoot,usedBoneIndices))
                {
                    if(o.automaticBones) report.warnings.Add(ApmLocalization.T("공유 본을 독립 복사합니다. 원본은 유지되며 원본 본의 개별 애니메이션은 연결되지 않습니다: ")+c.name);
                    else report.errors.Add(ApmLocalization.T("다른 Renderer가 전용 본 가지를 공유하거나 사용 여부를 검사할 수 없습니다: ")+c.name);
                }
                if(copied.Contains(c.transform) || supported.Contains(c)) continue;
                if(c.GetType().FullName==PhysBone)
                {
                    var serialized=new SerializedObject(c);var p=serialized.FindProperty("rootTransform");var controlled=p!=null ? p.objectReferenceValue as Transform : null; if(!controlled) controlled=c.transform;
                    bool overlaps=o.automaticBones ? copied.Any(t=>!IsBodyAttachment(o,t) && Controls(c,t)) : Within(o.boneRoot,controlled) || Within(controlled,o.boneRoot);
                    var ignores=serialized.FindProperty("ignoreTransforms");
                    if(overlaps && Within(o.boneRoot,controlled) && ignores!=null)
                        for(int i=0;i<ignores.arraySize;i++) { var ignored=ignores.GetArrayElementAtIndex(i).objectReferenceValue as Transform;if(ignored!=controlled && Within(o.boneRoot,ignored)) { overlaps=false;break; } }
                    if(overlaps) report.errors.Add(ApmLocalization.T("가지 밖의 PhysBone이 전용 본을 제어합니다: ")+c.name);
                }
                string type=c.GetType().FullName ?? "";
                if((type.Contains("Constraint") && !IsConstraint(c)) || type.Contains("Contact"))
                {
                    bool depends=c.transform==source.transform || References(c).Any(r=>(o.automaticBones ? copied.Contains(TransformOf(r.Value)):Within(TransformOf(r.Value),o.boneRoot)) || r.Value==source);
                    // A contact only reads positions; the original keeps its gimmick and the copy simply has none.
                    // An unknown constraint would move the copied bones, which the copy cannot reproduce.
                    if(depends && type.Contains("Contact")) report.warnings.Add(ApmLocalization.T("원본 Contact 기믹은 새 프리팹에 복사되지 않습니다: ")+c.name);
                    else if(depends) report.errors.Add(ApmLocalization.T("외부 Constraint/Contact 의존성이 있습니다: ")+c.name);
                }
                var animator=c as Animator;
                if(animator && animator.runtimeAnimatorController) CheckClips(animator.transform,animator.runtimeAnimatorController.animationClips,o,dependencies,report);
                var animation=c as Animation;
                if(animation) foreach(AnimationState state in animation) CheckClips(animation.transform,new[]{state.clip},o,dependencies,report);
                // Avatar FX controllers and other persistent controllers can be carried by serialized components.
                if(!(c is MonoBehaviour) || !o.avatarRoot || !Within(c.transform,o.avatarRoot)) continue;
                foreach(var r in References(c))
                {
                    var controller=r.Value as RuntimeAnimatorController;
                    if(controller) CheckClips(o.avatarRoot ? o.avatarRoot : c.transform,controller.animationClips,o,dependencies,report);
                }
            }
        }
        static void CheckClips(Transform owner, IEnumerable<AnimationClip> clips,ApmOptions o,HashSet<Transform> copied,ValidationReport report)
        {
            foreach(var clip in clips.Where(c=>c).Distinct())
            foreach(var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
            {
                var t=string.IsNullOrEmpty(binding.path)?owner:owner.Find(binding.path);
                // The copy is a new object no clip reaches: it keeps the edit pose without the original's toggles/gimmicks.
                if(copied.Contains(t)) { report.warnings.Add(ApmLocalization.T("원본 애니메이션(토글·기믹)은 새 프리팹에 연결되지 않습니다: ")+clip.name+" / "+binding.path);break; }
                // The original keeps its own toggle; the copy is a new object the clip never reaches.
                if(t==o.source.transform) { report.warnings.Add(ApmLocalization.T("원본 메쉬의 토글·쉐이프키 애니메이션은 새 프리팹에 연결되지 않습니다: ")+clip.name);break; }
            }
        }
        internal static IEnumerable<KeyValuePair<string,Object>> References(Component component)
        {
            var serialized=new SerializedObject(component);var p=serialized.GetIterator();
            while(p.Next(true)) if(p.propertyType==SerializedPropertyType.ObjectReference && !IsOwnershipProperty(p.propertyPath)) yield return new KeyValuePair<string,Object>(p.propertyPath,p.objectReferenceValue);
        }
        internal static bool IsOwnershipProperty(string path)
        { return path=="m_GameObject" || path=="m_Script" || path=="m_PrefabInstance" || path=="m_PrefabAsset" || path=="m_CorrespondingSourceObject"; }
        internal static bool IsExcludedCollider(ApmOptions o,Component owner,string path,Object value)
        { return o.excludeExternalColliders && owner.GetType().FullName==PhysBone && path.StartsWith("colliders.Array.data[",StringComparison.Ordinal) && value is Component && value.GetType().FullName==Collider && !Within(((Component)value).transform,o.boneRoot); }
    }
}
