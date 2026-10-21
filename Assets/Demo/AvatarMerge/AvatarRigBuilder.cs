using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    /// <summary>构建 Demo 共用骨架 + Idle 动画（Legacy Animation，避免 Editor 依赖）。</summary>
    public static class AvatarRigBuilder
    {
        public const string RigRootName = "AvatarRig";

        public static GameObject BuildRig(Transform parent = null)
        {
            var root = new GameObject(RigRootName);
            if (parent != null)
                root.transform.SetParent(parent, false);

            var bones = new Transform[AvatarPlaceholderFactory.RigBoneNames.Length];
            bones[0] = CreateBone(root.transform, "Root", Vector3.zero);
            bones[1] = CreateBone(bones[0], "Hips", new Vector3(0f, 0.9f, 0f));
            bones[2] = CreateBone(bones[1], "Spine", new Vector3(0f, 0.2f, 0f));
            bones[3] = CreateBone(bones[2], "Chest", new Vector3(0f, 0.2f, 0f));
            bones[4] = CreateBone(bones[3], "Neck", new Vector3(0f, 0.18f, 0f));
            bones[5] = CreateBone(bones[4], "Head", new Vector3(0f, 0.12f, 0f));

            bones[6] = CreateBone(bones[3], "L_UpperArm", new Vector3(-0.22f, 0.05f, 0f));
            bones[7] = CreateBone(bones[6], "L_LowerArm", new Vector3(-0.22f, 0f, 0f));
            bones[8] = CreateBone(bones[7], "L_Hand", new Vector3(-0.18f, 0f, 0f));

            bones[9] = CreateBone(bones[3], "R_UpperArm", new Vector3(0.22f, 0.05f, 0f));
            bones[10] = CreateBone(bones[9], "R_LowerArm", new Vector3(0.22f, 0f, 0f));
            bones[11] = CreateBone(bones[10], "R_Hand", new Vector3(0.18f, 0f, 0f));

            bones[12] = CreateBone(bones[1], "L_UpperLeg", new Vector3(-0.1f, -0.05f, 0f));
            bones[13] = CreateBone(bones[12], "L_LowerLeg", new Vector3(0f, -0.35f, 0f));
            bones[14] = CreateBone(bones[13], "L_Foot", new Vector3(0f, -0.35f, 0.05f));

            bones[15] = CreateBone(bones[1], "R_UpperLeg", new Vector3(0.1f, -0.05f, 0f));
            bones[16] = CreateBone(bones[15], "R_LowerLeg", new Vector3(0f, -0.35f, 0f));
            bones[17] = CreateBone(bones[16], "R_Foot", new Vector3(0f, -0.35f, 0.05f));

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var smr = body.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = null;
            smr.sharedMaterial = null;
            smr.bones = bones;
            smr.rootBone = bones[1];
            smr.quality = SkinQuality.Bone4;
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.receiveShadows = false;
            smr.updateWhenOffscreen = true;

            var anim = root.AddComponent<Animation>();
            var clip = CreateIdleClip();
            anim.AddClip(clip, clip.name);
            anim.clip = clip;
            anim.playAutomatically = true;
            anim.wrapMode = WrapMode.Loop;
            anim.Play(clip.name);

            return root;
        }

        public static Transform[] CollectBones(Transform rigRoot)
        {
            var bones = new Transform[AvatarPlaceholderFactory.RigBoneNames.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var t = FindDeep(rigRoot, AvatarPlaceholderFactory.RigBoneNames[i]);
                if (t == null)
                    throw new System.InvalidOperationException($"Missing bone: {AvatarPlaceholderFactory.RigBoneNames[i]}");
                bones[i] = t;
            }
            return bones;
        }

        public static SkinnedMeshRenderer GetBodyRenderer(Transform rigRoot)
        {
            var body = FindDeep(rigRoot, "Body");
            if (body != null)
                return body.GetComponent<SkinnedMeshRenderer>();
            return rigRoot.GetComponentInChildren<SkinnedMeshRenderer>();
        }

        public static Matrix4x4[] BuildBindposes(Transform smrTransform, Transform[] bones)
        {
            var bindposes = new Matrix4x4[bones.Length];
            var meshWorld = smrTransform.localToWorldMatrix;
            for (int i = 0; i < bones.Length; i++)
                bindposes[i] = bones[i].worldToLocalMatrix * meshWorld;
            return bindposes;
        }

        static Transform CreateBone(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        static AnimationClip CreateIdleClip()
        {
            var clip = new AnimationClip
            {
                name = "Idle",
                legacy = true,
                wrapMode = WrapMode.Loop
            };

            var chestZ = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.5f, 4f),
                new Keyframe(1f, 0f));
            var headX = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.5f, -5f),
                new Keyframe(1f, 0f));

            clip.SetCurve("Root/Hips/Spine/Chest", typeof(Transform), "localEulerAngles.z", chestZ);
            clip.SetCurve("Root/Hips/Spine/Chest/Neck/Head", typeof(Transform), "localEulerAngles.x", headX);
            return clip;
        }
    }
}
