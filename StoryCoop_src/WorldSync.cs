using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCoop
{
    /// <summary>Stable, cross-instance id for scene objects: "scene:Root/Child#1/Leaf".</summary>
    public static class ObjectId
    {
        public static string Of(GameObject go)
        {
            if (go == null) return null;
            var parts = new List<string>();
            Transform t = go.transform;
            while (t != null)
            {
                int dup = 0;
                Transform p = t.parent;
                if (p != null)
                {
                    for (int i = 0; i < p.childCount; i++)
                    {
                        Transform c = p.GetChild(i);
                        if (c == t) break;
                        if (c.name == t.name) dup++;
                    }
                }
                else
                {
                    GameObject[] roots = t.gameObject.scene.GetRootGameObjects();
                    for (int i = 0; i < roots.Length; i++)
                    {
                        if (roots[i].transform == t) break;
                        if (roots[i].name == t.name) dup++;
                    }
                }
                parts.Add(dup > 0 ? t.name + "#" + dup : t.name);
                t = p;
            }
            parts.Reverse();
            return go.scene.name + ":" + string.Join("/", parts.ToArray());
        }

        public static GameObject Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            int colon = id.IndexOf(':');
            if (colon < 0) return null;
            Scene scene = SceneManager.GetSceneByName(id.Substring(0, colon));
            if (!scene.IsValid() || !scene.isLoaded) return null;
            string[] parts = id.Substring(colon + 1).Split('/');
            string rootName; int rootIdx;
            Split(parts[0], out rootName, out rootIdx);
            Transform cur = null;
            int seen = 0;
            foreach (GameObject r in scene.GetRootGameObjects())
                if (r.name == rootName) { if (seen == rootIdx) { cur = r.transform; break; } seen++; }
            for (int k = 1; k < parts.Length && cur != null; k++)
            {
                string n; int idx;
                Split(parts[k], out n, out idx);
                Transform next = null; seen = 0;
                for (int i = 0; i < cur.childCount; i++)
                {
                    Transform c = cur.GetChild(i);
                    if (c.name == n) { if (seen == idx) { next = c; break; } seen++; }
                }
                cur = next;
            }
            return cur != null ? cur.gameObject : null;
        }

        static void Split(string part, out string name, out int idx)
        {
            idx = 0; name = part;
            int h = part.LastIndexOf('#');
            if (h > 0 && int.TryParse(part.Substring(h + 1), out idx)) name = part.Substring(0, h);
            else idx = 0;
        }
    }

    /// <summary>
    /// World sync:
    ///  - interactions (doors, ropes, notes, anything usable): ALWAYS synced. The object's own state machine gets the
    ///    same events, then its saved state is copied, and objects taken/destroyed are destroyed on the partner.
    ///  - scripted moments (story trigger zones): optional, both ways.
    ///  - loose physics objects (pickups lying around): host decides.
    /// </summary>
    public static class WorldSync
    {
        public const byte MsgInteract = 14, MsgObjState = 15, MsgDestroy = 16, MsgScripted = 17, MsgPickups = 18, MsgHostSettings = 19;

        static readonly BinaryFormatter bf = new BinaryFormatter();
        static readonly MethodInfo mCheckTarget = typeof(vgFilteredTriggerVolume).GetMethod("CheckForTargetCollision", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // pending state snapshots: saveable -> time to send
        static readonly List<KeyValuePair<vgSaveable, float>> pendingState = new List<KeyValuePair<vgSaveable, float>>();

        // pickups
        static readonly Dictionary<vgPickup, Vector3> lastPickupPos = new Dictionary<vgPickup, Vector3>();
        static readonly List<vgPickup> pickupCache = new List<vgPickup>();
        static float pickupScan, pickupSend;
        public static bool HostWorldObjects; // the effective value (host's choice)

        static CoopRunner R { get { return CoopRunner.Instance; } }

        static bool Active
        {
            get { return R != null && R.Net.Connected && !GameApi.Loading && (R.IsHost || R.Synced) && !CoopRunner.ApplyingRemote; }
        }

        public static bool IsLocalPlayer(GameObject go)
        {
            if (go == null) return false;
            GameObject p = vgPlayerController.playerGameObject;
            if (p != null && go.transform.root == p.transform.root) return true;
            Camera cam = Camera.main;
            return cam != null && go == cam.gameObject;
        }

        // =========================================================== outgoing (called from patches)

        public static void OnInteractEvent(vgTargetInteract ti, string eventName)
        {
            if (!Active || ti == null || string.IsNullOrEmpty(eventName)) return;
            // carried objects: only their final state/destruction is synced, never "use" them on the partner
            if (ti.GetComponent<vgPickup>() != null) { QueueState(ti.gameObject, 1f); return; }
            string id = ObjectId.Of(ti.gameObject);
            R.Net.Send(MsgInteract, Pack.Build(w => { w.Write(id); w.Write(eventName); }));
            Plugin.Log("Interact out: " + id + " -> " + eventName);
            QueueState(ti.gameObject, 0.4f);
            QueueState(ti.gameObject, 2.5f);
        }

        public static void OnInteractFinished(vgTargetInteract ti)
        {
            if (!Active || ti == null) return;
            QueueState(ti.gameObject, 0.3f);
            QueueState(ti.gameObject, 3f);
        }

        public static void OnSaveableDestroyed(string guid)
        {
            if (!Active || string.IsNullOrEmpty(guid)) return;
            GameObject go = vgSaveManager.FindGameObjectFromGUID(guid);
            string id = go != null ? ObjectId.Of(go) : "";
            R.Net.Send(MsgDestroy, Pack.Build(w => { w.Write(guid); w.Write(id); }));
            Plugin.Log("Destroy out: " + guid + " " + id);
        }

        public static void OnTriggerVolumeEnter(vgEventTriggerVolume vol, Collider col)
        {
            if (!Active || !Plugin.CfgShareScripted.Value || vol == null || col == null) return;
            if (!IsLocalPlayer(col.gameObject)) return;
            if (!vol.deleteAfterTriggeredEntered && !Plugin.CfgShareAllTriggers.Value) return;
            try { if (mCheckTarget != null && !(bool)mCheckTarget.Invoke(vol, new object[] { col })) return; } catch { }
            string id = ObjectId.Of(vol.gameObject);
            string ev = vol.triggerEnterEvent ?? "";
            R.Net.Send(MsgScripted, Pack.Build(w => { w.Write(id); w.Write(ev); }));
            Plugin.Log("Scripted out: " + id + " (" + ev + ")");
        }

        static void QueueState(GameObject go, float delay)
        {
            vgSaveable s = go.GetComponentInParent<vgSaveable>();
            if (s == null || s.GetComponent<vgPlayerController>() != null || s.GetComponentInChildren<vgPlayerController>() != null) return;
            pendingState.Add(new KeyValuePair<vgSaveable, float>(s, Time.unscaledTime + delay));
        }

        // =========================================================== tick

        public static void Tick()
        {
            if (R == null || !R.Net.Connected) { pendingState.Clear(); return; }

            for (int i = pendingState.Count - 1; i >= 0; i--)
            {
                if (pendingState[i].Value > Time.unscaledTime) continue;
                vgSaveable s = pendingState[i].Key;
                pendingState.RemoveAt(i);
                if (s != null && Active) SendState(s);
            }

            if (R.IsHost) HostWorldObjects = Plugin.CfgSyncWorldObjects.Value;
            if (HostWorldObjects && Active) PickupTick();
        }

        static void SendState(vgSaveable s)
        {
            try
            {
                var data = NewData();
                string guid = s.guid;
                vgSaveable.SaveObject(s.gameObject, s.recursiveSave, ref guid, data, s.savePlayMakers);
                byte[] blob;
                using (var ms = new MemoryStream()) { bf.Serialize(ms, data.nameSerializedDataMap); blob = ms.ToArray(); }
                string id = ObjectId.Of(s.gameObject);
                string g = guid ?? "";
                R.Net.Send(MsgObjState, Pack.Build(w => { w.Write(id); w.Write(g); w.Write(blob.Length); w.Write(blob); }));
                Plugin.Log("State out: " + id + " (" + blob.Length + "B)");
            }
            catch (Exception e) { Plugin.Log("SendState failed: " + e.Message); }
        }

        static vgSaveData NewData()
        {
            var d = new vgSaveData();
            d.nameSerializedDataMap = new Dictionary<string, byte[]>();
            d.loadedChunkNames = new List<string>();
            d.destroyedObjects = new List<string>();
            d.displayName = ""; d.imagePath = ""; d.loadedLevel = "";
            return d;
        }

        static void PickupTick()
        {
            pickupScan -= Time.unscaledDeltaTime;
            if (pickupScan <= 0f)
            {
                pickupScan = 2f;
                pickupCache.Clear();
                foreach (vgPickup p in UnityEngine.Object.FindObjectsOfType<vgPickup>()) pickupCache.Add(p);
            }
            pickupSend -= Time.unscaledDeltaTime;
            if (pickupSend > 0f) return;
            pickupSend = 0.15f;

            var moved = new List<vgPickup>();
            foreach (vgPickup p in pickupCache)
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                Vector3 last;
                Vector3 pos = p.transform.position;
                if (!lastPickupPos.TryGetValue(p, out last)) { lastPickupPos[p] = pos; continue; }
                if ((pos - last).sqrMagnitude > 0.0025f) { moved.Add(p); lastPickupPos[p] = pos; }
            }
            if (moved.Count == 0) return;
            R.Net.Send(MsgPickups, Pack.Build(w =>
            {
                w.Write(moved.Count);
                foreach (vgPickup p in moved)
                {
                    w.Write(ObjectId.Of(p.gameObject));
                    Vector3 v = p.transform.position; Quaternion q = p.transform.rotation;
                    w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                }
            }));
        }

        public static void SendHostSettings()
        {
            if (R == null || !R.IsHost || !R.Net.Connected) return;
            bool v = Plugin.CfgSyncWorldObjects.Value;
            R.Net.Send(MsgHostSettings, Pack.Build(w => w.Write(v)));
        }

        // =========================================================== incoming

        public static bool Handle(byte type, byte[] body)
        {
            switch (type)
            {
                case MsgInteract: Remote(() => ApplyInteract(body)); return true;
                case MsgObjState: Remote(() => ApplyState(body)); return true;
                case MsgDestroy: Remote(() => ApplyDestroy(body)); return true;
                case MsgScripted: Remote(() => ApplyScripted(body)); return true;
                case MsgPickups: Remote(() => ApplyPickups(body)); return true;
                case MsgHostSettings:
                    using (BinaryReader r = Pack.Reader(body)) HostWorldObjects = r.ReadBoolean();
                    Plugin.Log("Host world-object sync: " + HostWorldObjects);
                    return true;
            }
            return false;
        }

        static void Remote(Action a)
        {
            if (GameApi.Loading || (R.IsClient && !R.Synced)) return;
            CoopRunner.ApplyingRemote = true;
            try { a(); }
            catch (Exception e) { Plugin.Log("World apply failed: " + e); }
            finally { CoopRunner.ApplyingRemote = false; }
        }

        static void ApplyInteract(byte[] body)
        {
            string id, ev;
            using (BinaryReader r = Pack.Reader(body)) { id = r.ReadString(); ev = r.ReadString(); }
            GameObject go = ObjectId.Find(id);
            if (go == null) { Plugin.Log("Interact in: object not loaded " + id); return; }
            vgTargetInteract ti = go.GetComponent<vgTargetInteract>();
            if (ti != null && ti.stateMachine != null) ti.stateMachine.SendEvent(ev);
            Plugin.Log("Interact in: " + id + " -> " + ev);
        }

        static void ApplyState(byte[] body)
        {
            string id, senderGuid; byte[] blob;
            using (BinaryReader r = Pack.Reader(body)) { id = r.ReadString(); senderGuid = r.ReadString(); blob = r.ReadBytes(r.ReadInt32()); }
            GameObject go = ObjectId.Find(id);
            vgSaveable s = go != null ? go.GetComponent<vgSaveable>() : null;
            if (s == null && !string.IsNullOrEmpty(senderGuid))
            {
                GameObject g2 = vgSaveManager.FindGameObjectFromGUID(senderGuid);
                if (g2 != null) s = g2.GetComponent<vgSaveable>();
            }
            if (s == null) { Plugin.Log("State in: no saveable for " + id); return; }

            Dictionary<string, byte[]> map;
            using (var ms = new MemoryStream(blob)) map = (Dictionary<string, byte[]>)bf.Deserialize(ms);
            var data = NewData();
            foreach (var kv in map)
            {
                string key = kv.Key;
                // runtime-generated guids differ between games: re-key onto ours
                if (!string.IsNullOrEmpty(senderGuid) && senderGuid != s.guid && key.StartsWith(senderGuid))
                    key = s.guid + key.Substring(senderGuid.Length);
                data.nameSerializedDataMap[key] = kv.Value;
            }
            s.OnLoad(data);
            s.OnPostLoad(data);
            Plugin.Log("State in: " + id);
        }

        static void ApplyDestroy(byte[] body)
        {
            string guid, id;
            using (BinaryReader r = Pack.Reader(body)) { guid = r.ReadString(); id = r.ReadString(); }
            GameObject go = vgSaveManager.FindGameObjectFromGUID(guid);
            if (go == null) go = ObjectId.Find(id);
            if (go == null || IsLocalPlayer(go)) return;
            vgUtils.IntentionalDestroy(go);
            Plugin.Log("Destroy in: " + id);
        }

        static void ApplyScripted(byte[] body)
        {
            string id, ev;
            using (BinaryReader r = Pack.Reader(body)) { id = r.ReadString(); ev = r.ReadString(); }
            if (!Plugin.CfgShareScripted.Value) return;
            GameObject go = ObjectId.Find(id);
            vgEventTriggerVolume vol = go != null ? go.GetComponent<vgEventTriggerVolume>() : null;
            GameObject me = vgPlayerController.playerGameObject;
            if (vol == null || me == null) { Plugin.Log("Scripted in: volume not loaded " + id); return; }

            // replay vgEventTriggerVolume.OnTriggerEnter as if our own player walked in
            if (!string.IsNullOrEmpty(vol.triggerEnterEvent))
                vgEventManager.Instance.TriggerEvent(vol.triggerEnterEvent, vol.gameObject, me);
            if (!string.IsNullOrEmpty(vol.triggerFact))
                vgEventManager.Instance.SetFact(vol.triggerFact, true, vol.hint);
            if (vol.playmakerFSM != null)
            {
                FsmGameObject g = vol.playmakerFSM.FsmVariables.FindFsmGameObject("TriggeringGameObject");
                if (g != null) g.Value = me;
                vol.playmakerFSM.SendEvent(vol.triggerEnterEvent);
            }
            if (vol.deleteAfterTriggeredEntered) vgUtils.IntentionalDestroy(vol.gameObject);
            Plugin.Log("Scripted in: " + id + " (" + ev + ")");
            if (R != null) R.Notice("Shared moment from " + R.PartnerName);
        }

        static void ApplyPickups(byte[] body)
        {
            if (!HostWorldObjects) return;
            using (BinaryReader r = Pack.Reader(body))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    GameObject go = ObjectId.Find(id);
                    if (go == null) continue;
                    // don't yank things out of our own hands
                    GameObject me = vgPlayerController.playerGameObject;
                    if (me != null && go.transform.IsChildOf(me.transform)) continue;
                    vgPickup p = go.GetComponent<vgPickup>();
                    go.transform.position = v;
                    go.transform.rotation = q;
                    Rigidbody rb = go.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                    if (p != null) lastPickupPos[p] = v;
                }
            }
        }
    }

    // =============================================================== patches

    [HarmonyPatch(typeof(vgTargetInteract), "TriggerEvent")]
    static class Patch_InteractEvent
    {
        static void Postfix(vgTargetInteract __instance, string eventName)
        {
            try { if (!CoopRunner.ApplyingRemote) WorldSync.OnInteractEvent(__instance, eventName); } catch { }
        }
    }

    [HarmonyPatch(typeof(vgTargetInteract), "OnUseFinished")]
    static class Patch_InteractFinished
    {
        static void Prefix(vgTargetInteract __instance)
        {
            try { if (!CoopRunner.ApplyingRemote) WorldSync.OnInteractFinished(__instance); } catch { }
        }
    }

    [HarmonyPatch(typeof(vgSaveManager), "OnSaveableDestroyed")]
    static class Patch_SaveableDestroyed
    {
        static void Postfix(string guid)
        {
            try { if (!CoopRunner.ApplyingRemote) WorldSync.OnSaveableDestroyed(guid); } catch { }
        }
    }

    [HarmonyPatch(typeof(vgEventTriggerVolume), "OnTriggerEnter")]
    static class Patch_TriggerVolume
    {
        static void Prefix(vgEventTriggerVolume __instance, Collider collider)
        {
            try { if (!CoopRunner.ApplyingRemote) WorldSync.OnTriggerVolumeEnter(__instance, collider); } catch { }
        }
    }

    // "Don't pause": the pause menu still opens, but the world keeps running (and keeps syncing).
    [HarmonyPatch(typeof(vgPauseManager), "PauseStart")]
    static class Patch_PauseStart
    {
        static void Postfix()
        {
            if (!Plugin.CfgNoPause.Value) return;
            float ts = DevMenu.Instance != null ? DevMenu.Instance.timeScale : 1f;
            Time.timeScale = ts > 0f ? ts : 1f;
        }
    }

    [HarmonyPatch(typeof(vgPlayerController), "Pause")]
    static class Patch_PlayerPause
    {
        static void Postfix()
        {
            if (!Plugin.CfgNoPause.Value) return;
            try { if (vgAudioManager.Instance != null) vgAudioManager.Instance.Pause(false); } catch { }
        }
    }
}
