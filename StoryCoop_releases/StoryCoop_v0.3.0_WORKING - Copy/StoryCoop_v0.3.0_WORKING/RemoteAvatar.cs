using System;
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

        public static RemoteAvatar Create(string name)
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) return null;
            Transform model = GameApi.FindModel(pc.gameObject);
            if (model == null) { Plugin.Log("RemoteAvatar: player model not found"); return null; }

            GameObject go = (GameObject)Instantiate(model.gameObject);
            go.name = "StoryCoop_RemotePlayer";
            go.SetActive(false);
            Strip(go);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = 0;
                t.gameObject.SetActive(true);
            }
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.shadowCastingMode = ShadowCastingMode.On;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.updateWhenOffscreen = true;
            }

            var av = go.AddComponent<RemoteAvatar>();
            av.animator = go.GetComponent<Animator>();
            if (av.animator == null) av.animator = go.GetComponentInChildren<Animator>();
            if (av.animator != null)
            {
                av.animator.applyRootMotion = false;
                av.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            // model faces differently from camera; keep the local offset
            av.yawOffset = Mathf.DeltaAngle(pc.playerCamera != null ? pc.playerCamera.transform.eulerAngles.y : pc.transform.eulerAngles.y, model.eulerAngles.y);
            av.AddHeadAndLabel(name);
            go.SetActive(true);
            return av;
        }

        static void Strip(GameObject root)
        {
            // remove gameplay logic, keep visuals + animator
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
            Transform head = null;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n == "head" || n.EndsWith(":head") || n.EndsWith("_head") || n == "henryhead") { head = t; }
            }
            // First-person rig has no visible head, add a simple one so the partner isn't headless.
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediate(ball.GetComponent<Collider>());
            ball.name = "StoryCoop_Head";
            ball.transform.SetParent(head != null ? head : transform, false);
            ball.transform.localScale = Vector3.one * 0.24f;
            ball.transform.localPosition = head != null ? Vector3.zero : new Vector3(0f, 1.7f, 0f);
            Renderer br = ball.GetComponent<Renderer>();
            br.material.color = new Color(0.86f, 0.68f, 0.55f);

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
            if ((transform.position - targetPos).sqrMagnitude > 400f) transform.position = targetPos; // >20m: snap
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
