using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCoop
{
    public struct PlayerState
    {
        public string scene;
        public Vector3 rootPos;   // player controller position (used for teleport)
        public Vector3 modelPos;  // visual model position
        public float yaw;
        public float pitch;
        public float fwd, strafe;
        public int itemType;
        public byte flags; // 1 idle, 2 jog, 4 crouch
        public int day;

        public byte[] Encode()
        {
            PlayerState s = this;
            return Pack.Build(w =>
            {
                w.Write(s.scene ?? "");
                W(w, s.rootPos); W(w, s.modelPos);
                w.Write(s.yaw); w.Write(s.pitch); w.Write(s.fwd); w.Write(s.strafe);
                w.Write(s.itemType); w.Write(s.flags); w.Write(s.day);
            });
        }

        public static PlayerState Decode(byte[] d)
        {
            var s = new PlayerState();
            using (BinaryReader r = Pack.Reader(d))
            {
                s.scene = r.ReadString();
                s.rootPos = R(r); s.modelPos = R(r);
                s.yaw = r.ReadSingle(); s.pitch = r.ReadSingle(); s.fwd = r.ReadSingle(); s.strafe = r.ReadSingle();
                s.itemType = r.ReadInt32(); s.flags = r.ReadByte(); s.day = r.ReadInt32();
            }
            return s;
        }

        static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 R(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        public static bool Capture(out PlayerState s)
        {
            s = new PlayerState();
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) return false;
            s.scene = GameApi.SceneName;
            s.rootPos = pc.transform.position;
            Transform model = GameApi.FindModel(pc.gameObject);
            s.modelPos = model != null ? model.position : pc.transform.position;
            s.yaw = (model != null ? model : pc.transform).eulerAngles.y;
            if (pc.playerCamera != null)
            {
                float p = pc.playerCamera.transform.eulerAngles.x;
                if (p > 180f) p -= 360f;
                s.pitch = p;
                // first-person camera yaw is the real facing direction
                s.yaw = pc.playerCamera.transform.eulerAngles.y;
            }
            vgPlayerAnimationBridge ab = pc.animBridge;
            if (ab != null && ab.animator != null)
            {
                Animator a = ab.animator;
                s.fwd = a.GetFloat(vgPlayerAnimVariables.ForwardVelocityFloat);
                s.strafe = a.GetFloat(vgPlayerAnimVariables.StrafeVelocityFloat);
                s.itemType = a.GetInteger(vgPlayerAnimVariables.ItemTypeInt);
                if (a.GetBool(vgPlayerAnimVariables.IdleBool)) s.flags |= 1;
            }
            if (pc.IsJogging()) s.flags |= 2;
            if (pc.isCrouching) s.flags |= 4;
            s.day = vgEventManager.Instance != null ? (int)vgEventManager.Instance.GetCurrentDayValue() : -1;
            return true;
        }
    }

    /// <summary>
    /// Stable naming of the renderers under Henry's model so both games can talk about
    /// "the pulaski in his right hand" without sharing object IDs.
    /// Body meshes (skinned meshes directly under the model root) are always shown;
    /// everything else (held props, radio, map, backpack...) follows the owner's real visibility.
    /// </summary>
    public static class ModelParts
    {
        public static List<KeyValuePair<string, Renderer>> Props(Transform root)
        {
            var list = new List<KeyValuePair<string, Renderer>>();
            Walk(root, root, "", list);
            return list;
        }

        static void Walk(Transform root, Transform t, string path, List<KeyValuePair<string, Renderer>> list)
        {
            var dup = new Dictionary<string, int>();
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (c.name.StartsWith("StoryCoop_")) continue;
                if (c.GetComponent<vgPickup>() != null) continue; // world objects being carried: not part of the rig
                int n;
                dup.TryGetValue(c.name, out n);
                dup[c.name] = n + 1;
                string p = path + "/" + c.name + (n > 0 ? "#" + n : "");
                Renderer r = c.GetComponent<Renderer>();
                if (r != null && !IsBody(root, r)) list.Add(new KeyValuePair<string, Renderer>(p, r));
                Walk(root, c, p, list);
            }
        }

        public static bool IsBody(Transform root, Renderer r)
        {
            return r is SkinnedMeshRenderer && r.transform.parent == root;
        }

        public static List<string> VisibleProps(Transform root)
        {
            var vis = new List<string>();
            foreach (var kv in Props(root))
                if (kv.Value.enabled && kv.Value.gameObject.activeInHierarchy) vis.Add(kv.Key);
            return vis;
        }
    }

    /// <summary>Visual copy of the local Henry model, driven by the partner's state.</summary>
    public class RemoteAvatar : MonoBehaviour
    {
        Animator animator;
        Vector3 targetPos;
        float targetYaw;
        PlayerState last;
        bool has;
        TextMesh label;
        float yawOffset;
        readonly Dictionary<string, Renderer> props = new Dictionary<string, Renderer>();
        public bool Highlight;
        /// <summary>(child, parent) bone pairs for skeleton ESP.</summary>
        public readonly List<Transform[]> BonePairs = new List<Transform[]>();

        void BuildBones()
        {
            var bones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (ModelParts.IsBody(transform, smr) && smr.bones != null)
                    foreach (Transform b in smr.bones) if (b != null) bones.Add(b);
            foreach (Transform b in bones)
            {
                string n = b.name.ToLowerInvariant();
                if (n.Contains("finger") || n.Contains("thumb") || n.Contains("index") || n.Contains("middle") || n.Contains("ring") || n.Contains("pinky") || n.Contains("toe") || n.Contains("eye") || n.Contains("jaw") || n.Contains("twist") || n.Contains("end")) continue;
                Transform p = b.parent;
                while (p != null && !bones.Contains(p)) p = p.parent;
                if (p == null) continue;
                BonePairs.Add(new[] { b, p });
            }
            Plugin.Log("Skeleton bones: " + bones.Count + ", lines: " + BonePairs.Count);
        }

        public static RemoteAvatar Create(string name)
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) return null;
            Transform model = GameApi.FindModel(pc.gameObject);
            if (model == null) { Plugin.Log("RemoteAvatar: player model not found"); return null; }

            // what the local model shows right now = sensible default until the partner tells us
            var initial = new HashSet<string>(ModelParts.VisibleProps(model));

            GameObject go = (GameObject)Instantiate(model.gameObject);
            go.name = "StoryCoop_RemotePlayer";
            go.SetActive(false);
            // carried world objects (vgPickup) were reparented to the hand; never clone those
            foreach (vgPickup pk in go.GetComponentsInChildren<vgPickup>(true))
                if (pk != null) DestroyImmediate(pk.gameObject);
            Strip(go);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = 0;
                t.gameObject.SetActive(true); // visibility is controlled per renderer below
            }
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.On;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.updateWhenOffscreen = true;
                if (ModelParts.IsBody(go.transform, r)) r.enabled = true;
            }

            var av = go.AddComponent<RemoteAvatar>();
            foreach (var kv in ModelParts.Props(go.transform)) av.props[kv.Key] = kv.Value;
            av.ApplyVisible(initial);
            av.animator = go.GetComponent<Animator>();
            if (av.animator == null) av.animator = go.GetComponentInChildren<Animator>();
            if (av.animator != null)
            {
                av.animator.applyRootMotion = false;
                av.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            av.yawOffset = Mathf.DeltaAngle(pc.playerCamera != null ? pc.playerCamera.transform.eulerAngles.y : pc.transform.eulerAngles.y, model.eulerAngles.y);
            av.AddHeadAndLabel(name);
            av.BuildBones();
            go.SetActive(true);
            Plugin.Log("Avatar props tracked: " + av.props.Count + ", visible initially: " + initial.Count);
            return av;
        }

        public void ApplyVisible(HashSet<string> visible)
        {
            if (visible == null) return;
            foreach (var kv in props)
                if (kv.Value != null) kv.Value.enabled = visible.Contains(kv.Key);
        }

        static void Strip(GameObject root)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is Animator || c is Renderer || c is MeshFilter) continue;
                    if (c is Behaviour || c is Collider || c is Rigidbody || c is CharacterController || c is Joint || c is AudioSource)
                    {
                        try { DestroyImmediate(c); } catch { }
                    }
                }
            }
        }

        void AddHeadAndLabel(string name)
        {
            bool hasHeadMesh = false;
            foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (ModelParts.IsBody(transform, smr) && smr.name.ToLowerInvariant().Contains("head")) hasHeadMesh = true;

            Transform head = null;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n == "head" || n.EndsWith(":head") || n.EndsWith("_head") || n == "henryhead") head = t;
            }
            if (!hasHeadMesh && Plugin.CfgHeadSphere.Value)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                DestroyImmediate(ball.GetComponent<Collider>());
                ball.name = "StoryCoop_Head";
                ball.transform.SetParent(head != null ? head : transform, false);
                ball.transform.localScale = Vector3.one * 0.24f;
                ball.transform.localPosition = head != null ? Vector3.zero : new Vector3(0f, 1.7f, 0f);
                ball.GetComponent<Renderer>().material.color = new Color(0.86f, 0.68f, 0.55f);
            }

            var lg = new GameObject("StoryCoop_Label");
            lg.transform.SetParent(transform, false);
            lg.transform.localPosition = new Vector3(0f, 2.05f, 0f);
            label = lg.AddComponent<TextMesh>();
            label.text = name;
            label.characterSize = 0.05f;
            label.fontSize = 48;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.color = new Color(1f, 0.75f, 0.3f);
            try
            {
                Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (f != null) { label.font = f; lg.GetComponent<MeshRenderer>().sharedMaterial = f.material; }
            }
            catch { }
        }

        public void SetName(string n) { if (label != null) label.text = n; }
        public void ShowLabel(bool on) { if (label != null) label.gameObject.SetActive(on); }

        public void Push(PlayerState s)
        {
            if (!has)
            {
                transform.position = s.modelPos;
                transform.rotation = Quaternion.Euler(0f, s.yaw + yawOffset, 0f);
            }
            last = s;
            targetPos = s.modelPos;
            targetYaw = s.yaw + yawOffset;
            has = true;
        }

        void Update()
        {
            if (!has) return;
            if ((transform.position - targetPos).sqrMagnitude > 400f) transform.position = targetPos;
            transform.position = Vector3.Lerp(transform.position, targetPos, 1f - Mathf.Exp(-14f * Time.deltaTime));
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, targetYaw, 0f), 1f - Mathf.Exp(-12f * Time.deltaTime));

            if (animator != null && animator.runtimeAnimatorController != null)
            {
                try
                {
                    animator.SetBool(vgPlayerAnimVariables.IdleBool, (last.flags & 1) != 0);
                    animator.SetFloat(vgPlayerAnimVariables.ForwardVelocityFloat, last.fwd);
                    animator.SetFloat(vgPlayerAnimVariables.StrafeVelocityFloat, last.strafe);
                    animator.SetFloat(vgPlayerAnimVariables.SpeedMagnitudeFloat, Mathf.Max(Mathf.Abs(last.fwd), Mathf.Abs(last.strafe)));
                    animator.SetBool(vgPlayerAnimVariables.CrouchedBool, (last.flags & 4) != 0);
                    animator.SetInteger(vgPlayerAnimVariables.ItemTypeInt, last.itemType);
                }
                catch { }
            }

            Camera cam = Camera.main;
            if (label != null && cam != null)
                label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
        }
    }
}
