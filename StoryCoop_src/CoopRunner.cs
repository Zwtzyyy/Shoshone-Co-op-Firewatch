using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StoryCoop
{
    public class CoopRunner : MonoBehaviour
    {
        public static CoopRunner Instance;
        public static bool ApplyingRemote;

        readonly NetPeer net = new NetPeer();
        bool wasConnected;
        string partnerName = "";
        string partnerScene = "";
        int partnerDay = -1;
        PlayerState partnerState;
        bool havePartnerState;
        RemoteAvatar avatar;
        string avatarScene = "";

        // host
        bool snapshotPending;
        string snapshotReason = "";
        float stableTimer;

        // client
        bool synced;              // client has adopted the host's story
        bool loadFromSnapshot;    // the current load was started by us
        bool waitingSnapshot;
        readonly List<KeyValuePair<string, float>> bufferedFacts = new List<KeyValuePair<string, float>>();
        float factSettleTimer;

        // facts
        readonly Dictionary<string, float> known = new Dictionary<string, float>();
        vgBlackboard primedGlobal, primedDay;
        HashSet<string> excluded = new HashSet<string>();
        float factTimer;

        // misc
        vgSaveManager hookedSave;
        float sendTimer, pingTimer;
        bool showUI;
        string ipField, portField, nameField;
        string status = "Offline";
        readonly List<string> notices = new List<string>();
        float noticeTimer;
        string lastScene = "";

        public bool IsHost { get { return net.Mode == PeerMode.Host; } }
        public bool IsClient { get { return net.Mode == PeerMode.Client; } }
        public NetPeer Net { get { return net; } }
        public string Status { get { return status; } }
        public List<string> Notices { get { return notices; } }
        public float NoticeTimer { get { return noticeTimer; } set { noticeTimer = value; } }
        public bool Synced { get { return synced; } }
        public string PartnerName { get { return partnerName; } }
        public bool HavePartnerState { get { return havePartnerState && net.Connected; } }
        public PlayerState Partner { get { return partnerState; } }
        public RemoteAvatar Avatar { get { return avatar; } }
        public string IpField { get { return ipField; } set { ipField = value; } }
        public string PortField { get { return portField; } set { portField = value; } }
        public string NameField { get { return nameField; } set { nameField = value; } }
        public bool ShowUI { get { return showUI; } set { showUI = value; } }

        // visible prop renderers on our partner's model
        HashSet<string> partnerVisible;
        string lastVisualSig = "";
        float visualTimer, visualForceTimer;

        void Awake()
        {
            Instance = this;
            ipField = Plugin.CfgIp.Value;
            portField = Plugin.CfgPort.Value.ToString();
            nameField = Plugin.CfgName.Value;
            showUI = Plugin.CfgShowUI.Value;
            foreach (string f in Plugin.CfgExcludedFacts.Value.Split(','))
                if (f.Trim().Length > 0) excluded.Add(f.Trim());
        }

        void OnDestroy()
        {
            Plugin.Log("Runner destroyed");
            net.Shutdown();
        }

        public void Notice(string s)
        {
            Plugin.Log("[notice] " + s);
            notices.Add(s);
            if (notices.Count > 5) notices.RemoveAt(0);
            noticeTimer = 8f;
        }

        // =============================================================== main loop

        void Update()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.Log("Tick error: " + e); }
        }

        void Tick()
        {
            HookSaveManager();

            string scene = GameApi.SceneName;
            if (scene != lastScene)
            {
                Plugin.Log("Scene: " + lastScene + " -> " + scene);
                lastScene = scene;
                DestroyAvatar();
                factSettleTimer = 2f;
            }

            if (net.Connected && !wasConnected) OnConnected();
            if (!net.Connected && wasConnected) OnDisconnected();
            wasConnected = net.Connected;

            byte type; byte[] body;
            int budget = 200;
            while (budget-- > 0 && net.Poll(out type, out body))
            {
                try { Handle(type, body); }
                catch (Exception e) { Plugin.Log("Handle " + type + " failed: " + e); }
            }

            if (!net.Connected) { status = net.Mode == PeerMode.Host ? "Hosting - waiting for partner on port " + portField : "Offline"; UpdateAvatar(); return; }

            status = (IsHost ? "Host" : "Client") + " | partner: " + partnerName + " (" + partnerScene + ", day " + partnerDay + ")"
                     + (IsClient ? (synced ? " | synced" : waitingSnapshot ? " | waiting for host state" : " | not synced") : "");

            // keys
            if (Input.GetKeyDown(Plugin.KeyBring.Value)) BringPartner();
            if (Input.GetKeyDown(Plugin.KeyGoto.Value)) GoToPartner();
            if (IsClient && Input.GetKeyDown(Plugin.KeyResync.Value)) RequestSnapshot("manual");

            WorldSync.Tick();
            HostSnapshotTick();
            ClientFactBufferTick();
            FactTick();
            PlayerTick();
            UpdateAvatar();

            pingTimer -= Time.unscaledDeltaTime;
            if (pingTimer <= 0f) { pingTimer = 3f; net.Send(Msg.Ping, null); }
        }

        void HookSaveManager()
        {
            vgSaveManager sm = vgSaveManager.Instance;
            if (sm == hookedSave) return;
            if (hookedSave != null)
            {
                hookedSave.OnLoadStart -= OnLoadStart;
                hookedSave.OnLoadFinish -= OnLoadFinish;
            }
            hookedSave = sm;
            if (sm != null)
            {
                sm.OnLoadStart += OnLoadStart;
                sm.OnLoadFinish += OnLoadFinish;
            }
        }

        // =============================================================== connection

        void OnConnected()
        {
            Plugin.Log("Connected (" + net.Mode + ")");
            known.Clear(); primedGlobal = null; primedDay = null;
            if (IsClient)
            {
                synced = false;
                string n = nameField;
                net.Send(Msg.Hello, Pack.Build(w => { w.Write(Msg.Proto); w.Write(n); w.Write(GameApi.SceneName); }));
            }
        }

        void OnDisconnected()
        {
            Notice("Partner disconnected");
            DestroyAvatar();
            havePartnerState = false;
            partnerName = ""; partnerScene = ""; partnerDay = -1;
            partnerVisible = null; lastVisualSig = "";
            synced = false; waitingSnapshot = false; snapshotPending = false;
        }

        void Handle(byte type, byte[] body)
        {
            switch (type)
            {
                case Msg.Hello:
                    using (BinaryReader r = Pack.Reader(body))
                    {
                        int proto = r.ReadInt32();
                        partnerName = r.ReadString();
                        partnerScene = r.ReadString();
                        if (proto != Msg.Proto) Notice("Version mismatch! partner proto " + proto + " vs " + Msg.Proto);
                    }
                    Notice(partnerName + " joined");
                    {
                        string n = nameField; string sc = GameApi.SceneName;
                        net.Send(Msg.Welcome, Pack.Build(w => { w.Write(Msg.Proto); w.Write(n); w.Write(sc); }));
                    }
                    WorldSync.SendHostSettings();
                    if (!GameApi.InMainMenu) QueueSnapshot("partner joined");
                    else net.Send(Msg.Notice, Pack.Build(w => w.Write("Host is in the main menu. Wait for the host to start New Game / Continue.")));
                    break;

                case Msg.Welcome:
                    using (BinaryReader r = Pack.Reader(body))
                    {
                        r.ReadInt32();
                        partnerName = r.ReadString();
                        partnerScene = r.ReadString();
                    }
                    Notice("Connected to host " + partnerName + " (" + partnerScene + ")");
                    if (partnerScene != "Main_Menu") waitingSnapshot = true;
                    break;

                case Msg.PlayerState:
                    partnerState = PlayerState.Decode(body);
                    havePartnerState = true;
                    partnerScene = partnerState.scene;
                    partnerDay = partnerState.day;
                    if (avatar != null) avatar.Push(partnerState);
                    break;

                case Msg.Facts:
                    ReceiveFacts(body);
                    break;

                case Msg.Snapshot:
                    if (IsClient) ReceiveSnapshot(body);
                    break;

                case Msg.RequestSnapshot:
                    if (IsHost) QueueSnapshot("client requested");
                    break;

                case Msg.NewGame:
                    if (IsClient && Plugin.CfgFollowNewGame.Value)
                    {
                        if (GameApi.StartNewGameAsClient())
                        {
                            synced = true;
                            known.Clear(); primedGlobal = null; primedDay = null;
                            Notice("Host started a new story - starting it here too");
                        }
                        else
                        {
                            Notice("Host started a new story; you're not in the main menu, pulling host state instead");
                            RequestSnapshot("host new game");
                        }
                    }
                    break;

                case Msg.Dialog:
                    ReceiveDialog(body);
                    break;

                case Msg.Visuals:
                    using (BinaryReader r = Pack.Reader(body))
                    {
                        int n = r.ReadInt32();
                        var set = new HashSet<string>();
                        for (int i = 0; i < n; i++) set.Add(r.ReadString());
                        partnerVisible = set;
                    }
                    if (avatar != null) avatar.ApplyVisible(partnerVisible);
                    break;

                case Msg.TeleportTo:
                    using (BinaryReader r = Pack.Reader(body))
                    {
                        string sc = r.ReadString();
                        Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float yaw = r.ReadSingle();
                        if (sc == GameApi.SceneName && GameApi.WorldStable) { GameApi.Teleport(p, yaw); Notice("Teleported to " + partnerName); }
                        else Notice("Partner tried to bring you, but you're in a different scene/loading");
                    }
                    break;

                case Msg.Notice:
                    using (BinaryReader r = Pack.Reader(body)) Notice(r.ReadString());
                    break;

                case Msg.Bye:
                    net.Shutdown();
                    break;

                case Msg.Ping:
                    break;

                default:
                    if (!WorldSync.Handle(type, body)) Plugin.Log("Unknown message " + type);
                    break;
            }
        }

        // =============================================================== snapshots (host -> client)

        public void QueueSnapshot(string why)
        {
            if (!IsHost) return;
            snapshotPending = true;
            snapshotReason = why;
            stableTimer = 0f;
            Plugin.Log("Snapshot queued: " + why);
        }

        void HostSnapshotTick()
        {
            if (!IsHost || !snapshotPending) return;
            if (!GameApi.WorldStable) { stableTimer = 0f; return; }
            stableTimer += Time.unscaledDeltaTime;
            if (stableTimer < 1.5f) return;
            snapshotPending = false;
            try
            {
                string info;
                byte[] snap = GameApi.BuildSnapshot(out info);
                // prime facts so the client doesn't receive a duplicate burst right after
                PrimeFacts();
                net.Send(Msg.Snapshot, snap);
                Plugin.Log("Snapshot sent (" + snapshotReason + "): " + info);
                Notice("Sent story state to " + partnerName);
            }
            catch (Exception e)
            {
                Plugin.Log("Snapshot build failed: " + e);
                Notice("Snapshot failed: " + e.Message);
            }
        }

        public void RequestSnapshot(string why)
        {
            if (!IsClient) return;
            waitingSnapshot = true;
            net.Send(Msg.RequestSnapshot, null);
            Plugin.Log("Requested snapshot: " + why);
        }

        void ReceiveSnapshot(byte[] body)
        {
            Plugin.Log("Snapshot received " + (body.Length / 1024) + "KB");
            if (GameApi.Loading)
            {
                // try again once our own load is done
                Plugin.Log("Busy loading, will re-request");
                StartCoroutine(RetryRequest());
                return;
            }
            try
            {
                bufferedFacts.Clear();
                loadFromSnapshot = true;
                DestroyAvatar();
                GameApi.ApplySnapshot(body);
                Notice("Loading host's story state...");
            }
            catch (Exception e)
            {
                loadFromSnapshot = false;
                Plugin.Log("ApplySnapshot failed: " + e);
                Notice("Could not load host state: " + e.Message);
            }
        }

        System.Collections.IEnumerator RetryRequest()
        {
            while (GameApi.Loading) yield return null;
            yield return new WaitForSeconds(1f);
            RequestSnapshot("retry");
        }

        void OnLoadStart(vgSaveData data)
        {
            DestroyAvatar();
            bufferedFacts.Clear();
            primedGlobal = null; primedDay = null;
            bool dayTransition = vgSaveManager.Instance != null && vgSaveManager.Instance.IsDayTransition();
            Plugin.Log("Load start (dayTransition=" + dayTransition + ", fromSnapshot=" + loadFromSnapshot + ")");
        }

        void OnLoadFinish(vgSaveData data)
        {
            bool dayTransition = vgSaveManager.Instance != null && vgSaveManager.Instance.IsDayTransition();
            Plugin.Log("Load finished (dayTransition=" + dayTransition + ", fromSnapshot=" + loadFromSnapshot + ")");
            factSettleTimer = 2f;
            if (!net.Connected) { loadFromSnapshot = false; return; }

            if (IsHost)
            {
                // host loaded a save, finished a day transition, etc. -> client follows
                QueueSnapshot(dayTransition ? "host day transition" : "host loaded");
            }
            else if (IsClient)
            {
                if (loadFromSnapshot)
                {
                    loadFromSnapshot = false;
                    synced = true;
                    waitingSnapshot = false;
                    GameApi.EnsureClientSlot();
                    Notice("Synced with host");
                }
                else if (!dayTransition && Plugin.CfgClientAutoResync.Value && synced)
                {
                    RequestSnapshot("client loaded its own save");
                }
                // client day transitions: wait, the host's own transition will send a snapshot
            }
        }

        public void OnLocalNewGame()
        {
            Plugin.Log("Local New Game");
            bufferedFacts.Clear();
            known.Clear(); primedGlobal = null; primedDay = null;
            if (IsHost && net.Connected)
            {
                net.Send(Msg.NewGame, null);
                Notice("Told " + partnerName + " to start the story");
            }
        }

        // =============================================================== facts

        bool FactsActive
        {
            get
            {
                if (!Plugin.CfgSyncFacts.Value || !net.Connected) return false;
                if (vgEventManager.Instance == null || GameApi.Loading || GameApi.InMainMenu) return false;
                if (factSettleTimer > 0f) return false;
                if (IsClient && !synced) return false;
                return true;
            }
        }

        void PrimeFacts()
        {
            vgBlackboard g = GameApi.GlobalBoard, d = GameApi.DayBoard;
            known.Clear();
            Collect(g, GameApi.BoardGlobal, null);
            Collect(d, GameApi.BoardDay, null);
            primedGlobal = g; primedDay = d;
        }

        void Collect(vgBlackboard b, byte board, List<KeyValuePair<string, float>> changes)
        {
            if (b == null || b.Facts == null) return;
            string prefix = board == GameApi.BoardDay ? "D|" : "G|";
            List<vgBlackboardFact> facts = b.Facts;
            for (int i = 0; i < facts.Count; i++)
            {
                vgBlackboardFact f = facts[i];
                if (f == null || string.IsNullOrEmpty(f.name) || excluded.Contains(f.name)) continue;
                string key = prefix + f.name;
                float old;
                if (known.TryGetValue(key, out old) && old == f.value) continue;
                known[key] = f.value;
                if (changes != null) changes.Add(new KeyValuePair<string, float>(key, f.value));
            }
        }

        void FactTick()
        {
            if (factSettleTimer > 0f) factSettleTimer -= Time.unscaledDeltaTime;
            if (!FactsActive) return;
            factTimer -= Time.unscaledDeltaTime;
            if (factTimer > 0f) return;
            factTimer = 0.2f;

            vgBlackboard g = GameApi.GlobalBoard, d = GameApi.DayBoard;
            if (g != primedGlobal || d != primedDay) { PrimeFacts(); return; }

            var changes = new List<KeyValuePair<string, float>>();
            Collect(g, GameApi.BoardGlobal, changes);
            Collect(d, GameApi.BoardDay, changes);
            if (changes.Count == 0) return;
            net.Send(Msg.Facts, Pack.Build(w =>
            {
                w.Write(changes.Count);
                foreach (var kv in changes) { w.Write(kv.Key); w.Write(kv.Value); }
            }));
            if (changes.Count <= 8)
            {
                var sb = new System.Text.StringBuilder("Facts out:");
                foreach (var kv in changes) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                Plugin.Log(sb.ToString());
            }
            else Plugin.Log("Facts out: " + changes.Count);
        }

        void ReceiveFacts(byte[] body)
        {
            var list = new List<KeyValuePair<string, float>>();
            using (BinaryReader r = Pack.Reader(body))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) list.Add(new KeyValuePair<string, float>(r.ReadString(), r.ReadSingle()));
            }
            if (!FactsActive)
            {
                // client still loading / settling: keep them for later (snapshot replaces them anyway)
                if (IsClient && synced || IsHost) bufferedFacts.AddRange(list);
                return;
            }
            ApplyFacts(list);
        }

        void ClientFactBufferTick()
        {
            if (bufferedFacts.Count == 0 || !FactsActive) return;
            if (primedGlobal == null) PrimeFacts();
            var copy = new List<KeyValuePair<string, float>>(bufferedFacts);
            bufferedFacts.Clear();
            ApplyFacts(copy);
        }

        void ApplyFacts(List<KeyValuePair<string, float>> list)
        {
            ApplyingRemote = true;
            try
            {
                foreach (var kv in list)
                {
                    string key = kv.Key;
                    if (key.Length < 3) continue;
                    byte board = key[0] == 'D' ? GameApi.BoardDay : GameApi.BoardGlobal;
                    string name = key.Substring(2);
                    if (excluded.Contains(name)) continue;
                    known[key] = kv.Value; // so we don't echo it back
                    try { GameApi.ApplyFact(board, name, kv.Value); }
                    catch (Exception e) { Plugin.Log("ApplyFact " + key + " failed: " + e.Message); }
                }
            }
            finally { ApplyingRemote = false; }
            if (list.Count <= 8)
            {
                var sb = new System.Text.StringBuilder("Facts in:");
                foreach (var kv in list) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                Plugin.Log(sb.ToString());
            }
            else Plugin.Log("Facts in: " + list.Count);
        }

        // =============================================================== dialog

        public void OnLocalDialogChoice(string dialogName, int index, string response, bool radio)
        {
            if (!Plugin.CfgSyncDialog.Value || !net.Connected) return;
            if (IsClient && !synced) return;
            net.Send(Msg.Dialog, Pack.Build(w => { w.Write(dialogName); w.Write(index); w.Write(response); w.Write(radio); }));
            Plugin.Log("Dialog out: " + dialogName + " #" + index + " -> " + response);
        }

        void ReceiveDialog(byte[] body)
        {
            string dialogName, response; int index; bool radio;
            using (BinaryReader r = Pack.Reader(body))
            {
                dialogName = r.ReadString(); index = r.ReadInt32(); response = r.ReadString(); radio = r.ReadBoolean();
            }
            Plugin.Log("Dialog in: " + dialogName + " #" + index + " -> " + response);
            if (!Plugin.CfgSyncDialog.Value || GameApi.Loading) return;
            ApplyingRemote = true;
            try
            {
                if (GameApi.ApplyDialogChoice(dialogName, index, response))
                    Notice(partnerName + " answered for both of you");
                else if (Plugin.CfgDialogFallback.Value && radio && vgEventManager.Instance != null && !string.IsNullOrEmpty(response))
                {
                    vgEventManager.Instance.TriggerEvent(response);
                    Plugin.Log("Dialog fallback: triggered " + response);
                }
            }
            catch (Exception e) { Plugin.Log("Dialog apply failed: " + e); }
            finally { ApplyingRemote = false; }
        }

        // =============================================================== players

        void PlayerTick()
        {
            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer > 0f) return;
            sendTimer = 1f / Mathf.Max(1f, Plugin.CfgSendRate.Value);
            PlayerState s;
            if (!GameApi.Loading && PlayerState.Capture(out s)) net.Send(Msg.PlayerState, s.Encode());
            VisualTick();
        }

        void VisualTick()
        {
            visualTimer -= Time.unscaledDeltaTime;
            visualForceTimer -= Time.unscaledDeltaTime;
            if (visualTimer > 0f) return;
            visualTimer = 0.25f;
            if (GameApi.Loading) return;
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) return;
            Transform model = GameApi.FindModel(pc.gameObject);
            if (model == null) return;
            List<string> vis = ModelParts.VisibleProps(model);
            string sig = string.Join("|", vis.ToArray());
            if (sig == lastVisualSig && visualForceTimer > 0f) return;
            lastVisualSig = sig;
            visualForceTimer = 5f;
            net.Send(Msg.Visuals, Pack.Build(w => { w.Write(vis.Count); foreach (string v in vis) w.Write(v); }));
        }

        void UpdateAvatar()
        {
            bool want = net.Connected && havePartnerState && partnerState.scene == GameApi.SceneName
                        && GameApi.WorldStable && !string.IsNullOrEmpty(partnerState.scene);
            if (!want) { DestroyAvatar(); return; }
            if (avatar == null || avatarScene != GameApi.SceneName)
            {
                DestroyAvatar();
                avatar = RemoteAvatar.Create(string.IsNullOrEmpty(partnerName) ? "Partner" : partnerName);
                avatarScene = GameApi.SceneName;
                if (avatar != null)
                {
                    avatar.Push(partnerState);
                    if (partnerVisible != null) avatar.ApplyVisible(partnerVisible);
                    Plugin.Log("Avatar created in " + avatarScene);
                }
            }
        }

        void DestroyAvatar()
        {
            if (avatar != null) Destroy(avatar.gameObject);
            avatar = null;
            avatarScene = "";
        }

        public void BringPartner()
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) return;
            Vector3 p = pc.transform.position + pc.transform.forward * 1.5f;
            float yaw = pc.transform.eulerAngles.y + 180f;
            string sc = GameApi.SceneName;
            net.Send(Msg.TeleportTo, Pack.Build(w => { w.Write(sc); w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(yaw); }));
            Notice("Pulling " + partnerName + " to you");
        }

        public void GoToPartner()
        {
            if (!havePartnerState || partnerState.scene != GameApi.SceneName || !GameApi.WorldStable) { Notice("Partner not in your scene"); return; }
            GameApi.Teleport(partnerState.rootPos - Quaternion.Euler(0f, partnerState.yaw, 0f) * Vector3.forward * 1.5f, partnerState.yaw);
            Notice("Teleported to " + partnerName);
        }

        // =============================================================== API for the menu

        public int Port { get { int p; return int.TryParse(portField, out p) ? p : 7778; } }

        public void Host()
        {
            SaveCfg();
            if (net.StartHost(Port)) Notice("Hosting on port " + Port); else Notice("Host failed: " + net.LastError);
        }

        public void Join()
        {
            SaveCfg();
            if (net.StartClient(ipField.Trim(), Port)) Notice("Connecting to " + ipField); else Notice("Join failed: " + net.LastError);
        }

        public void Leave()
        {
            net.Send(Msg.Bye, null);
            System.Threading.Thread.Sleep(50);
            net.Shutdown();
            if (wasConnected) OnDisconnected();
            wasConnected = false;
            Notice("Left session");
        }

        public void SendNotice(string text)
        {
            net.Send(Msg.Notice, Pack.Build(w => w.Write(text)));
        }

        void SaveCfg()
        {
            int port = Port;
            Plugin.CfgIp.Value = ipField.Trim();
            Plugin.CfgName.Value = nameField;
            Plugin.CfgPort.Value = port;
        }
    }
}
