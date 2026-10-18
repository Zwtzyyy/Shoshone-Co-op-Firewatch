using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace StoryCoop
{
    /// <summary>The "Shoshone Co-op" menu: main-menu button, in-game window, cheats, ESP and console.</summary>
    public class DevMenu : MonoBehaviour
    {
        public static DevMenu Instance;

        // ------------------------------------------------------------ window state
        bool open;
        int tab;
        static readonly string[] Tabs = { "Co-op", "Player", "World", "Story", "ESP", "Console" };
        Rect win = new Rect(0, 0, 820, 600);
        bool winPlaced;
        Vector2 scroll0, scroll1, scroll2, scroll3, scroll4, scroll5, scroll6;
        vgInputContext blockContext;
        bool contextPushed;

        // ------------------------------------------------------------ cheats
        public float speedMult = 1f;
        public bool noclip;
        public float noclipSpeed = 12f;
        public float timeScale = 1f;
        public bool fovOverride;
        public float fov = 70f;
        public bool devHud = true;
        vgPlayerNavigationController speedNav;
        float[] baseSpeeds;
        bool noclipApplied;

        // ------------------------------------------------------------ ESP
        public bool espPartner = true, espPickups, espInteract, espTeleports, espArrow = true, espSkeleton = true;
        public Color colPartner, colSkeleton, colPickups, colInteract, colTeleports;
        BepInEx.Configuration.ConfigEntry<string> cfgColPartner, cfgColSkeleton, cfgColPickups, cfgColInteract, cfgColTeleports;
        string colorEditing = "";
        static readonly Color[] Swatches = {
            new Color(1f,0.25f,0.22f), new Color(1f,0.55f,0.18f), new Color(1f,0.9f,0.2f), new Color(0.3f,1f,0.4f),
            new Color(0.2f,0.95f,0.95f), new Color(0.3f,0.55f,1f), new Color(0.7f,0.35f,1f), new Color(1f,0.4f,0.8f),
            Color.white, new Color(0.55f,0.55f,0.55f) };
        public float espRange = 150f;
        readonly List<Component> pickups = new List<Component>();
        readonly List<Component> interacts = new List<Component>();
        readonly List<Component> teleports = new List<Component>();
        float espRefresh;

        // ------------------------------------------------------------ story / console
        string factSearch = "", factEditName = "", factEditValue = "", eventField = "", consoleInput = "";
        static readonly List<string> eventLog = new List<string>();
        static readonly List<string> logLines = new List<string>();
        static readonly object logLock = new object();
        bool logUnity = true;
        string posName = "spot";
        readonly List<string[]> savedPositions = new List<string[]>();
        string posFile;

        // ------------------------------------------------------------ main menu button
        GameObject menuButton;
        float menuButtonRetry;
        bool menuButtonLogged;

        // ------------------------------------------------------------ styles
        GUIStyle sWindow, sTab, sTabOn, sBtn, sLabel, sHeader, sSmall, sBox, sField, sEsp, sToggle;
        Texture2D texWin, texTab, texTabOn, texBtn, texBtnHover, texBox, texField, texWhite;
        static readonly Color Accent = new Color(1f, 0.55f, 0.18f);

        CoopRunner R { get { return CoopRunner.Instance; } }

        void Awake()
        {
            Instance = this;
            posFile = Path.Combine(Paths.ConfigPath, "StoryCoop_positions.txt");
            LoadPositions();
            Application.logMessageReceived += OnUnityLog;
            AddLog("<color=#ff8c2e><b>Firewatch Story Co-op v" + Plugin.Version + " - " + Plugin.Credit + "</b></color>");
            open = Plugin.CfgShowUI.Value;
            var cfg = Plugin.Instance.Config;
            cfgColPartner = cfg.Bind("ESP", "PartnerColor", "FF8C2E", "hex RGB");
            cfgColSkeleton = cfg.Bind("ESP", "SkeletonColor", "FFFFFF", "hex RGB");
            cfgColPickups = cfg.Bind("ESP", "PickupColor", "66FF80", "hex RGB");
            cfgColInteract = cfg.Bind("ESP", "InteractColor", "66CCFF", "hex RGB");
            cfgColTeleports = cfg.Bind("ESP", "TeleportColor", "FF66FF", "hex RGB");
            colPartner = Hex(cfgColPartner.Value); colSkeleton = Hex(cfgColSkeleton.Value); colPickups = Hex(cfgColPickups.Value);
            colInteract = Hex(cfgColInteract.Value); colTeleports = Hex(cfgColTeleports.Value);
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnUnityLog;
        }

        // ================================================================= logging

        void OnUnityLog(string msg, string stack, LogType type)
        {
            if (!logUnity) return;
            string p = type == LogType.Error || type == LogType.Exception ? "<color=#ff6b6b>[ERR]</color> "
                     : type == LogType.Warning ? "<color=#ffd166>[WRN]</color> " : "[unity] ";
            AddLog(p + Escape(msg));
        }

        static string Escape(string s) { return s == null ? "" : s.Replace("<", "‹").Replace(">", "›"); }

        public static void AddLog(string line)
        {
            lock (logLock)
            {
                logLines.Add(DateTime.Now.ToString("HH:mm:ss ") + line);
                if (logLines.Count > 400) logLines.RemoveRange(0, logLines.Count - 400);
            }
        }

        public static void LogEvent(string name)
        {
            lock (logLock)
            {
                eventLog.Add(DateTime.Now.ToString("HH:mm:ss ") + name);
                if (eventLog.Count > 200) eventLog.RemoveRange(0, eventLog.Count - 200);
            }
        }

        // ================================================================= update

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(Plugin.KeyUI.Value)) SetOpen(!open);
                if (open && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);

                if (GameApi.InMainMenu) EnsureMenuButton(); else menuButton = null;

                ApplySpeed();
                ApplyNoclip();
                if (timeScale != 1f && Time.timeScale > 0f && Mathf.Abs(Time.timeScale - timeScale) > 0.001f && !GameApi.Loading) Time.timeScale = timeScale;

                espRefresh -= Time.unscaledDeltaTime;
                if (espRefresh <= 0f && (espPickups || espInteract || espTeleports || tab == 1))
                {
                    espRefresh = 2f;
                    RefreshEspLists();
                }

                if (open && !GameApi.InMainMenu && vgInputManager.Instance != null && !contextPushed) PushBlock();
                if ((!open || GameApi.InMainMenu) && contextPushed) PopBlock();
            }
            catch (Exception e) { Plugin.Log("DevMenu update: " + e.Message); }
        }

        public static readonly Color OnGreen = new Color(0.25f, 0.9f, 0.35f);
        public static readonly Color OffRed = new Color(0.95f, 0.25f, 0.22f);
        readonly List<Graphic> menuButtonTexts = new List<Graphic>();

        void LateUpdate()
        {
            // main menu button: green = hosting/connected, red = offline. Done in LateUpdate so
            // the menu's own highlight scripts can't paint over it.
            if (menuButton != null && R != null)
            {
                if (menuButtonTexts.Count == 0)
                    foreach (Graphic g in menuButton.GetComponentsInChildren<Graphic>(true))
                        if (g is Text || g.GetType().Name.Contains("TextMeshPro")) menuButtonTexts.Add(g);
                Color c = R.Net.Mode != PeerMode.Offline ? OnGreen : OffRed;
                foreach (Graphic g in menuButtonTexts) if (g != null && g.color != c) g.color = c;
            }
            else menuButtonTexts.Clear();

            if (fovOverride && Camera.main != null) Camera.main.fieldOfView = fov;
        }

        public void SetOpen(bool v)
        {
            open = v;
            if (!open) PopBlock();
        }

        void PushBlock()
        {
            try
            {
                if (blockContext == null)
                {
                    blockContext = ScriptableObject.CreateInstance<vgInputContext>();
                    blockContext.name = "StoryCoop_MenuContext";
                    blockContext.masksOtherInput = true;
                    blockContext.locksCursor = false;
                    blockContext.uiContext = true;
                    blockContext.hidesReticule = true;
                    blockContext.disableSaveLoad = false;
                }
                vgInputManager.Instance.PushContext(blockContext);
                contextPushed = true;
            }
            catch (Exception e) { Plugin.Log("PushContext failed: " + e.Message); contextPushed = true; }
        }

        void PopBlock()
        {
            if (!contextPushed) return;
            contextPushed = false;
            try { if (vgInputManager.Instance != null && blockContext != null) vgInputManager.Instance.PopContext(blockContext); }
            catch (Exception e) { Plugin.Log("PopContext failed: " + e.Message); }
            if (!GameApi.InMainMenu) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        // ================================================================= main menu button

        void EnsureMenuButton()
        {
            if (menuButton != null) return;
            menuButtonRetry -= Time.unscaledDeltaTime;
            if (menuButtonRetry > 0f) return;
            menuButtonRetry = 1f;
            try
            {
                GameObject mm = GameObject.Find("Main Menu");
                if (mm == null) return;
                Transform group = mm.transform.Find("Main Menu Group");
                Transform settings = group != null ? group.Find("Settings Button") : null;
                if (settings == null)
                {
                    if (!menuButtonLogged) { Plugin.Log("Main menu: Settings Button not found"); menuButtonLogged = true; }
                    return;
                }
                Transform existing = group.Find("StoryCoop Button");
                if (existing != null) { menuButton = existing.gameObject; return; }

                GameObject b = (GameObject)Instantiate(settings.gameObject, settings.parent, false);
                b.name = "StoryCoop Button";
                b.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);

                // The original mod's glitches: persistent onClick (still opened Settings), PlayMaker
                // events on the clone, and the localizer rewriting the label back to "Settings".
                foreach (Component c in b.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string tn = c.GetType().Name;
                    if (c is PlayMakerFSM || tn.Contains("Localiz") || tn == "EventTrigger" || tn.Contains("PlayMaker"))
                        DestroyImmediate(c);
                }
                var kinds = new List<string>();
                foreach (Component c in b.GetComponentsInChildren<Component>(true)) if (c != null) kinds.Add(c.GetType().Name);
                Plugin.Log("Menu button components after cleanup: " + string.Join(", ", kinds.ToArray()));
                Button btn = b.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick = new Button.ButtonClickedEvent();
                    btn.onClick.AddListener(() => { SetOpen(true); tab = 0; });
                }
                SetAnyText(b, "Shoshone Co-op");
                b.SetActive(false);
                b.SetActive(true);
                menuButton = b;
                Plugin.Log("Main menu button added");
            }
            catch (Exception e) { Plugin.Log("Menu button failed: " + e.Message); }
        }

        static void SetAnyText(GameObject root, string text)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                Text t = c as Text;
                if (t != null) { t.text = text; continue; }
                if (c.GetType().Name.Contains("TextMeshPro"))
                {
                    PropertyInfo p = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (p != null && p.CanWrite) p.SetValue(c, text, null);
                }
            }
        }

        // ================================================================= cheats

        void ApplySpeed()
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            vgPlayerNavigationController nav = pc != null ? pc.GetComponent<vgPlayerNavigationController>() : null;
            if (nav == null) { speedNav = null; return; }
            if (nav != speedNav)
            {
                speedNav = nav;
                baseSpeeds = new[] { nav.walkSpeed, nav.walkBackwardSpeed, nav.strafeSpeed, nav.jogSpeed, nav.jogBackwardSpeed, nav.jogStrafeSpeed };
            }
            float m = speedMult;
            nav.walkSpeed = baseSpeeds[0] * m; nav.walkBackwardSpeed = baseSpeeds[1] * m; nav.strafeSpeed = baseSpeeds[2] * m;
            nav.jogSpeed = baseSpeeds[3] * m; nav.jogBackwardSpeed = baseSpeeds[4] * m; nav.jogStrafeSpeed = baseSpeeds[5] * m;
        }

        void ApplyNoclip()
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) { noclipApplied = false; return; }
            vgPlayerNavigationController nav = pc.GetComponent<vgPlayerNavigationController>();
            CharacterController cc = pc.GetComponent<CharacterController>();
            if (noclip != noclipApplied)
            {
                noclipApplied = noclip;
                if (nav != null) nav.enabled = !noclip;
                if (cc != null) cc.enabled = !noclip;
                R.Notice(noclip ? "Noclip ON (WASD, Space/Ctrl up/down, Shift fast)" : "Noclip OFF");
            }
            if (!noclip || open) return;
            Camera cam = pc.playerCamera != null ? pc.playerCamera : Camera.main;
            if (cam == null) return;
            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += cam.transform.forward;
            if (Input.GetKey(KeyCode.S)) dir -= cam.transform.forward;
            if (Input.GetKey(KeyCode.D)) dir += cam.transform.right;
            if (Input.GetKey(KeyCode.A)) dir -= cam.transform.right;
            if (Input.GetKey(KeyCode.Space)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl)) dir -= Vector3.up;
            float sp = noclipSpeed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f);
            pc.transform.position += dir.normalized * sp * Time.unscaledDeltaTime;
        }

        void Teleport(Vector3 p, float yaw)
        {
            if (noclip) { vgPlayerController pc = GameApi.LocalPlayer; if (pc != null) pc.transform.position = p; }
            else GameApi.Teleport(p, yaw);
        }

        void GiveItem(string ev, string label)
        {
            if (vgEventManager.Instance == null) return;
            vgEventManager.Instance.TriggerEvent(ev);
            R.Notice("Gave " + label);
        }

        void SetTimeOfDay(int h)
        {
            if (vgEventManager.Instance == null) return;
            vgEventManager.Instance.TriggerEvent("SetCurrentTime" + h.ToString("00"));
            R.Notice("Time set to " + h + ":00");
        }

        void NextDay()
        {
            if (R.Net.Connected && R.IsClient) { R.Notice("Only the host can skip days in co-op"); return; }
            if (vgDayManager.Instance == null) { R.Notice("No day manager in this scene"); return; }
            vgDayManager.Instance.StartNewDay();
            R.Notice("Starting next day...");
        }

        // ================================================================= ESP

        void RefreshEspLists()
        {
            pickups.Clear(); interacts.Clear(); teleports.Clear();
            if (GameApi.InMainMenu || GameApi.Loading) return;
            foreach (vgPickup p in FindObjectsOfType<vgPickup>()) pickups.Add(p);
            foreach (vgTargetInteract t in FindObjectsOfType<vgTargetInteract>()) if (t.GetComponent<vgPickup>() == null) interacts.Add(t);
            foreach (vgDebugTeleportDestination d in FindObjectsOfType<vgDebugTeleportDestination>()) teleports.Add(d);
        }

        void DrawEsp()
        {
            Camera cam = Camera.main;
            if (cam == null || GameApi.InMainMenu) return;
            Vector3 me = cam.transform.position;

            if (espPartner && R != null && R.HavePartnerState && R.Partner.scene == GameApi.SceneName)
            {
                Vector3 p = R.Partner.modelPos + Vector3.up * 1.0f;
                float d = Vector3.Distance(me, p);
                if (!DrawMarker(cam, p, R.PartnerName + "  " + d.ToString("0") + "m", colPartner, true, 1.8f) && espArrow)
                    DrawEdgeArrow(cam, p, R.PartnerName + " " + d.ToString("0") + "m");
            }
            if (espSkeleton && R != null && R.Avatar != null) DrawSkeleton(cam, R.Avatar);
            if (espPickups) DrawList(cam, me, pickups, colPickups, false);
            if (espInteract) DrawList(cam, me, interacts, colInteract, false);
            if (espTeleports) DrawList(cam, me, teleports, colTeleports, true);
        }

        void DrawSkeleton(Camera cam, RemoteAvatar av)
        {
            foreach (Transform[] pair in av.BonePairs)
            {
                if (pair[0] == null || pair[1] == null) continue;
                Vector3 a = cam.WorldToScreenPoint(pair[0].position), b = cam.WorldToScreenPoint(pair[1].position);
                if (a.z <= 0f || b.z <= 0f) continue;
                DrawLine(new Vector2(a.x, Screen.height - a.y), new Vector2(b.x, Screen.height - b.y), colSkeleton, 2f);
            }
        }

        void DrawLine(Vector2 a, Vector2 b, Color c, float w)
        {
            float len = (b - a).magnitude;
            if (len < 0.5f) return;
            Matrix4x4 m = GUI.matrix;
            Color old = GUI.color;
            float ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.color = new Color(0, 0, 0, 0.6f * c.a);
            GUI.DrawTexture(new Rect(a.x, a.y - w / 2 - 1, len, w + 2), texWhite);
            GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - w / 2, len, w), texWhite);
            GUI.matrix = m;
            GUI.color = old;
        }

        void DrawList(Camera cam, Vector3 me, List<Component> list, Color col, bool ignoreRange)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Component c = list[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                Vector3 p = c.transform.position;
                float d = Vector3.Distance(me, p);
                if (!ignoreRange && d > espRange) continue;
                DrawMarker(cam, p, Clean(c.gameObject.name) + " " + d.ToString("0") + "m", col, false, 0.3f);
            }
        }

        static string Clean(string n)
        {
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i).Trim() : n;
        }

        bool DrawMarker(Camera cam, Vector3 world, string text, Color col, bool box, float height)
        {
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f) return false;
            float x = s.x, y = Screen.height - s.y;
            if (x < -50 || x > Screen.width + 50 || y < -50 || y > Screen.height + 50) return false;
            Color old = GUI.color;
            if (box)
            {
                Vector3 top = cam.WorldToScreenPoint(world + Vector3.up * (height * 0.5f));
                Vector3 bot = cam.WorldToScreenPoint(world - Vector3.up * (height * 0.6f));
                float h = Mathf.Abs(top.y - bot.y), w = h * 0.45f;
                float ty = Screen.height - top.y;
                GUI.color = col;
                Line(new Rect(x - w / 2, ty, w, 2)); Line(new Rect(x - w / 2, ty + h, w, 2));
                Line(new Rect(x - w / 2, ty, 2, h)); Line(new Rect(x + w / 2, ty, 2, h + 2));
                y = ty - 18;
            }
            else
            {
                GUI.color = col;
                Line(new Rect(x - 3, y - 3, 6, 6));
                y -= 20;
            }
            GUI.color = Color.black;
            GUI.Label(new Rect(x - 149, y + 1, 300, 20), text, sEsp);
            GUI.color = col;
            GUI.Label(new Rect(x - 150, y, 300, 20), text, sEsp);
            GUI.color = old;
            return true;
        }

        void DrawEdgeArrow(Camera cam, Vector3 world, string text)
        {
            Vector3 local = cam.transform.InverseTransformPoint(world);
            Vector2 dir = new Vector2(local.x, local.y);
            if (local.z < 0) dir = -dir;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector2.down;
            dir.Normalize();
            Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float r = Mathf.Min(Screen.width, Screen.height) * 0.42f;
            Vector2 p = c + new Vector2(dir.x, -dir.y) * r;
            Color old = GUI.color;
            GUI.color = colPartner;
            Line(new Rect(p.x - 6, p.y - 6, 12, 12));
            GUI.Label(new Rect(p.x - 100, p.y + 8, 200, 20), text, sEsp);
            GUI.color = old;
        }

        void Line(Rect r) { GUI.DrawTexture(r, texWhite); }

        // ================================================================= GUI

        void BuildStyles()
        {
            if (sWindow != null) return;
            texWin = Tex(new Color(0.07f, 0.08f, 0.09f, 0.96f));
            texTab = Tex(new Color(0.14f, 0.15f, 0.17f, 1f));
            texTabOn = Tex(new Color(0.85f, 0.42f, 0.12f, 1f));
            texBtn = Tex(new Color(0.18f, 0.19f, 0.22f, 1f));
            texBtnHover = Tex(new Color(0.30f, 0.22f, 0.15f, 1f));
            texBox = Tex(new Color(0.11f, 0.12f, 0.14f, 1f));
            texField = Tex(new Color(0.03f, 0.03f, 0.04f, 1f));
            texWhite = Tex(Color.white);

            sWindow = new GUIStyle(GUI.skin.window);
            sWindow.normal.background = texWin; sWindow.onNormal.background = texWin;
            sWindow.normal.textColor = Accent; sWindow.onNormal.textColor = Accent;
            sWindow.fontSize = 15; sWindow.fontStyle = FontStyle.Bold;
            sWindow.padding = new RectOffset(12, 12, 30, 12);

            sTab = new GUIStyle(GUI.skin.button);
            sTab.normal.background = texTab; sTab.hover.background = texBtnHover; sTab.active.background = texTabOn;
            sTab.normal.textColor = new Color(0.8f, 0.8f, 0.8f); sTab.hover.textColor = Color.white;
            sTab.fontSize = 13; sTab.fixedHeight = 30;
            sTabOn = new GUIStyle(sTab);
            sTabOn.normal.background = texTabOn; sTabOn.normal.textColor = Color.white; sTabOn.fontStyle = FontStyle.Bold;

            sBtn = new GUIStyle(GUI.skin.button);
            sBtn.normal.background = texBtn; sBtn.hover.background = texBtnHover; sBtn.active.background = texTabOn;
            sBtn.normal.textColor = new Color(0.9f, 0.9f, 0.9f); sBtn.hover.textColor = Color.white;
            sBtn.fontSize = 12; sBtn.fixedHeight = 24; sBtn.margin = new RectOffset(3, 3, 3, 3);

            sLabel = new GUIStyle(GUI.skin.label); sLabel.normal.textColor = new Color(0.85f, 0.85f, 0.85f); sLabel.fontSize = 12; sLabel.richText = true; sLabel.wordWrap = true;
            sSmall = new GUIStyle(sLabel); sSmall.fontSize = 11; sSmall.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            sHeader = new GUIStyle(sLabel); sHeader.fontSize = 14; sHeader.fontStyle = FontStyle.Bold; sHeader.normal.textColor = Accent; sHeader.margin = new RectOffset(4, 4, 8, 4);
            sBox = new GUIStyle(GUI.skin.box); sBox.normal.background = texBox; sBox.padding = new RectOffset(8, 8, 6, 6);
            sField = new GUIStyle(GUI.skin.textField); sField.normal.background = texField; sField.focused.background = texField; sField.normal.textColor = Color.white; sField.focused.textColor = Color.white; sField.fontSize = 12;
            sToggle = new GUIStyle(GUI.skin.toggle); sToggle.fontSize = 12;
            sToggle.normal.textColor = OffRed; sToggle.hover.textColor = OffRed; sToggle.active.textColor = OffRed; sToggle.focused.textColor = OffRed;
            sToggle.onNormal.textColor = OnGreen; sToggle.onHover.textColor = OnGreen; sToggle.onActive.textColor = OnGreen; sToggle.onFocused.textColor = OnGreen;
            sEsp = new GUIStyle(GUI.skin.label); sEsp.alignment = TextAnchor.MiddleCenter; sEsp.fontSize = 12; sEsp.fontStyle = FontStyle.Bold;
        }

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        void OnGUI()
        {
            if (R == null) return;
            BuildStyles();
            GUI.depth = -1000;

            DrawEsp();
            DrawHud();
            if (GameApi.InMainMenu) DrawMenuCredit();

            if (!open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (!winPlaced) { win.x = (Screen.width - win.width) / 2; win.y = (Screen.height - win.height) / 2; winPlaced = true; }
            win = GUI.Window(0x5C00, win, DrawWindow, "SHOSHONE CO-OP  ·  DEV  v" + Plugin.Version + "  ·  " + Plugin.Credit, sWindow);
        }

        void DrawMenuCredit()
        {
            var st = new GUIStyle(sLabel);
            st.fontSize = 20; st.fontStyle = FontStyle.Bold; st.wordWrap = false; st.alignment = TextAnchor.LowerRight;
            Rect r = new Rect(Screen.width - 520, Screen.height - 70, 500, 30);
            Rect r2 = new Rect(Screen.width - 520, Screen.height - 44, 500, 24);
            var st2 = new GUIStyle(st); st2.fontSize = 13; st2.fontStyle = FontStyle.Normal;
            Color c = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), Plugin.Credit, st);
            GUI.Label(new Rect(r2.x + 1, r2.y + 1, r2.width, r2.height), "Firewatch Story Co-op v" + Plugin.Version + "  -  F8 for menu", st2);
            GUI.color = Accent;
            GUI.Label(r, Plugin.Credit, st);
            GUI.color = new Color(0.9f, 0.9f, 0.9f);
            GUI.Label(r2, "Firewatch Story Co-op v" + Plugin.Version + "  -  F8 for menu", st2);
            GUI.color = c;
        }

        void DrawHud()
        {
            float y = 8;
            if (R.NoticeTimer > 0f && R.Notices.Count > 0)
            {
                R.NoticeTimer -= Time.unscaledDeltaTime;
                Shadow(new Rect(10, y, 900, 22), "<color=#ff8c2e>[Co-op]</color> " + R.Notices[R.Notices.Count - 1]);
                y += 20;
            }
            if (!devHud || GameApi.InMainMenu || open) return;
            vgPlayerController pc = GameApi.LocalPlayer;
            string line = string.Format("{0:0} fps  |  {1}  |  day {2}", 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime), GameApi.SceneName,
                vgEventManager.Instance != null ? vgEventManager.Instance.GetCurrentDayValue().ToString("0") : "?");
            if (pc != null) { Vector3 p = pc.transform.position; line += string.Format("  |  {0:0.0} {1:0.0} {2:0.0}", p.x, p.y, p.z); }
            if (R.Net.Mode != PeerMode.Offline) line += "  |  " + R.Status;
            if (noclip) line += "  |  <color=#ff8c2e>NOCLIP</color>";
            if (speedMult != 1f) line += "  |  speed x" + speedMult.ToString("0.0");
            Shadow(new Rect(10, y, 1400, 22), line);
        }

        void Shadow(Rect r, string text)
        {
            GUIStyle st = sLabel; bool ww = st.wordWrap; st.wordWrap = false;
            Color c = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), StripTags(text), st);
            GUI.color = c; GUI.Label(r, text, st);
            st.wordWrap = ww;
        }

        static string StripTags(string s)
        {
            var sb = new System.Text.StringBuilder(); bool tag = false;
            foreach (char ch in s) { if (ch == '<') tag = true; else if (ch == '>') tag = false; else if (!tag) sb.Append(ch); }
            return sb.ToString();
        }

        void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Tabs.Length; i++)
                if (GUILayout.Button(Tabs[i], i == tab ? sTabOn : sTab)) tab = i;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            switch (tab)
            {
                case 0: scroll0 = GUILayout.BeginScrollView(scroll0); TabCoop(); GUILayout.EndScrollView(); break;
                case 1: scroll1 = GUILayout.BeginScrollView(scroll1); TabPlayer(); GUILayout.EndScrollView(); break;
                case 2: scroll2 = GUILayout.BeginScrollView(scroll2); TabWorld(); GUILayout.EndScrollView(); break;
                case 3: TabStory(); break;
                case 4: scroll4 = GUILayout.BeginScrollView(scroll4); TabEsp(); GUILayout.EndScrollView(); break;
                case 5: TabConsole(); break;
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Plugin.KeyUI.Value + " / Esc close   ·   " + Plugin.KeyBring.Value + " bring   ·   " + Plugin.KeyGoto.Value + " go to   ·   " + Plugin.KeyResync.Value + " resync", sSmall);
            if (GUILayout.Button("Close", sBtn, GUILayout.Width(80))) SetOpen(false);
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        bool Btn(string t, params GUILayoutOption[] o) { return GUILayout.Button(t, sBtn, o); }
        void H(string t) { GUILayout.Label(t, sHeader); }
        void L(string t) { GUILayout.Label(t, sLabel); }
        bool Tog(bool v, string t) { return GUILayout.Toggle(v, " " + t, sToggle); }
        string Field(string v, float w) { return GUILayout.TextField(v ?? "", sField, GUILayout.Width(w)); }

        float Slider(string label, float v, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, sLabel, GUILayout.Width(140));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(380));
            GUILayout.Label(v.ToString(fmt), sLabel, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            return v;
        }

        // ----------------------------------------------------------------- tabs

        void TabCoop()
        {
            NetPeer net = R.Net;
            GUILayout.BeginVertical(sBox);
            H("Connection");
            L("<b>Status:</b> " + R.Status);
            GUILayout.BeginHorizontal(); GUILayout.Label("Name", sLabel, GUILayout.Width(50)); R.NameField = Field(R.NameField, 220); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("IP", sLabel, GUILayout.Width(50)); R.IpField = Field(R.IpField, 220);
            GUILayout.Label("Port", sLabel, GUILayout.Width(40)); R.PortField = Field(R.PortField, 70); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = net.Mode == PeerMode.Offline;
            if (Btn("Host", GUILayout.Width(120))) R.Host();
            if (Btn("Join", GUILayout.Width(120))) R.Join();
            GUI.enabled = net.Mode != PeerMode.Offline;
            if (Btn("Leave", GUILayout.Width(120))) R.Leave();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(net.LastError)) L("<color=#ff6b6b>Last error: " + Escape(net.LastError) + "</color>");
            GUILayout.EndVertical();

            if (net.Connected)
            {
                GUILayout.BeginVertical(sBox);
                H("Partner");
                if (R.HavePartnerState)
                {
                    PlayerState p = R.Partner;
                    vgPlayerController pc = GameApi.LocalPlayer;
                    string dist = pc != null && p.scene == GameApi.SceneName ? Vector3.Distance(pc.transform.position, p.rootPos).ToString("0") + " m" : "different scene";
                    L(string.Format("<b>{0}</b> — {1}, day {2}, {3}", Escape(R.PartnerName), p.scene, p.day, dist));
                }
                GUILayout.BeginHorizontal();
                if (Btn("Bring partner to me")) R.BringPartner();
                if (Btn("Teleport to partner")) R.GoToPartner();
                if (R.IsClient && Btn("Resync from host")) R.RequestSnapshot("manual");
                if (R.IsHost && Btn("Push my state to client")) R.QueueSnapshot("manual");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ping partner:", sLabel, GUILayout.Width(90));
                if (Btn("Wave", GUILayout.Width(90))) R.SendNotice(R.NameField + " waves at you");
                if (Btn("Come here!", GUILayout.Width(90))) R.SendNotice(R.NameField + ": come here!");
                if (Btn("Wait for me", GUILayout.Width(100))) R.SendNotice(R.NameField + ": wait for me!");
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.BeginVertical(sBox);
            H("Sync options");
            Plugin.CfgSyncFacts.Value = Tog(Plugin.CfgSyncFacts.Value, "Sync story facts (both ways)");
            Plugin.CfgSyncDialog.Value = Tog(Plugin.CfgSyncDialog.Value, "Sync dialog / radio choices");
            Plugin.CfgDialogFallback.Value = Tog(Plugin.CfgDialogFallback.Value, "Play partner's reply even if I don't have that dialog open");
            Plugin.CfgFollowNewGame.Value = Tog(Plugin.CfgFollowNewGame.Value, "Client follows host's New Game");
            Plugin.CfgClientAutoResync.Value = Tog(Plugin.CfgClientAutoResync.Value, "Client re-pulls host state after loading its own save");
            Plugin.CfgShareScripted.Value = Tog(Plugin.CfgShareScripted.Value, "Share scripted moments (story triggers, Delilah calls) both ways");
            GUI.enabled = Plugin.CfgShareScripted.Value;
            Plugin.CfgShareAllTriggers.Value = Tog(Plugin.CfgShareAllTriggers.Value, "    ...also repeatable trigger zones (experimental)");
            GUI.enabled = true;
            L("<color=#9a9a9a>Doors, ropes, notes and anything you use always sync.</color>");
            if (!R.Net.Connected || R.IsHost)
            {
                bool w = Tog(Plugin.CfgSyncWorldObjects.Value, "Sync world objects (things you can pick up / throw)   [host decides]");
                if (w != Plugin.CfgSyncWorldObjects.Value) { Plugin.CfgSyncWorldObjects.Value = w; WorldSync.SendHostSettings(); }
            }
            else
            {
                GUI.enabled = false;
                Tog(WorldSync.HostWorldObjects, "Sync world objects   [set by host]");
                GUI.enabled = true;
            }
            Plugin.CfgNoPause.Value = Tog(Plugin.CfgNoPause.Value, "Don't pause the game when I press Esc (pause menu still opens)");
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("Log");
            for (int i = R.Notices.Count - 1; i >= 0; i--) L("• " + Escape(R.Notices[i]));
            GUILayout.EndVertical();
        }

        void TabPlayer()
        {
            vgPlayerController pc = GameApi.LocalPlayer;
            if (pc == null) { L("No player in this scene (main menu / loading / text intro)."); return; }
            Vector3 pos = pc.transform.position;

            GUILayout.BeginVertical(sBox);
            H("Movement");
            L(string.Format("Position  <b>{0:0.00}, {1:0.00}, {2:0.00}</b>   yaw {3:0}", pos.x, pos.y, pos.z, pc.transform.eulerAngles.y));
            speedMult = Slider("Speed multiplier", speedMult, 0.25f, 10f, "0.00x");
            GUILayout.BeginHorizontal();
            if (Btn("1x", GUILayout.Width(50))) speedMult = 1f;
            if (Btn("2x", GUILayout.Width(50))) speedMult = 2f;
            if (Btn("4x", GUILayout.Width(50))) speedMult = 4f;
            if (Btn("8x", GUILayout.Width(50))) speedMult = 8f;
            GUILayout.EndHorizontal();
            noclip = Tog(noclip, "Noclip / fly  (WASD, Space up, Ctrl down, Shift = fast)");
            noclipSpeed = Slider("Fly speed", noclipSpeed, 2f, 80f, "0");
            GUILayout.BeginHorizontal();
            if (Btn("Unstuck (+3 m up)")) Teleport(pos + Vector3.up * 3f, pc.transform.eulerAngles.y);
            if (Btn("Copy position to console")) { consoleInput = string.Format(CultureInfo.InvariantCulture, "tp {0:0.00} {1:0.00} {2:0.00}", pos.x, pos.y, pos.z); tab = 5; }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("Saved positions (" + GameApi.SceneName + ")");
            GUILayout.BeginHorizontal();
            posName = Field(posName, 200);
            if (Btn("Save current", GUILayout.Width(120))) SavePosition(posName, pos, pc.transform.eulerAngles.y);
            GUILayout.EndHorizontal();
            for (int i = 0; i < savedPositions.Count; i++)
            {
                string[] s = savedPositions[i];
                if (s[0] != GameApi.SceneName) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(Escape(s[1]) + "   (" + s[2] + ", " + s[3] + ", " + s[4] + ")", sLabel, GUILayout.Width(420));
                if (Btn("Go", GUILayout.Width(60))) Teleport(new Vector3(F(s[2]), F(s[3]), F(s[4])), F(s[5]));
                if (Btn("Delete", GUILayout.Width(70))) { savedPositions.RemoveAt(i); WritePositions(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("Developer teleport points (from the game's debug data)");
            if (teleports.Count == 0) L("None in the currently loaded area.");
            foreach (Component t in teleports)
            {
                if (t == null) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(Clean(t.name), sLabel, GUILayout.Width(420));
                if (Btn("Teleport", GUILayout.Width(90))) Teleport(t.transform.position, t.transform.eulerAngles.y);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        void TabWorld()
        {
            GUILayout.BeginVertical(sBox);
            H("Time");
            timeScale = Slider("Game speed", timeScale, 0.1f, 5f, "0.00x");
            GUILayout.BeginHorizontal();
            if (Btn("Normal", GUILayout.Width(80))) { timeScale = 1f; Time.timeScale = 1f; }
            if (Btn("Slow-mo", GUILayout.Width(80))) timeScale = 0.25f;
            if (Btn("Fast", GUILayout.Width(80))) timeScale = 3f;
            GUILayout.EndHorizontal();
            L("Time of day (game's own debug events):");
            for (int row = 0; row < 3; row++)
            {
                GUILayout.BeginHorizontal();
                for (int h = row * 8 + 1; h <= Mathf.Min(23, row * 8 + 8); h++)
                    if (Btn(h.ToString("00") + ":00", GUILayout.Width(80))) SetTimeOfDay(h);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (Btn("Skip to next day (host)", GUILayout.Width(220))) NextDay();
            GUILayout.Label("Plays the real day transition; client follows by snapshot.", sSmall);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("Give items (dev events)");
            GUILayout.BeginHorizontal();
            if (Btn("Flashlight")) GiveItem("EnableFlashlight", "flashlight");
            if (Btn("Rope")) GiveItem("OnEnablePlayerRope", "rope");
            if (Btn("Camera")) GiveItem("OnEnablePlayerCamera", "camera");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Btn("Pulaski")) GiveItem("OnEnablePlayerPulaski", "pulaski");
            if (Btn("Wave receiver")) GiveItem("OnEnablePlayerWaveReceiver", "wave receiver");
            if (Btn("Cave key")) GiveItem("OnGetCaveKey", "cave key");
            GUILayout.EndHorizontal();
            if (Btn("Give everything")) { GiveItem("EnableFlashlight", "flashlight"); GiveItem("OnEnablePlayerRope", "rope"); GiveItem("OnEnablePlayerCamera", "camera"); GiveItem("OnEnablePlayerPulaski", "pulaski"); GiveItem("OnEnablePlayerWaveReceiver", "wave receiver"); GiveItem("OnGetCaveKey", "cave key"); }
            L("<color=#ffd166>Giving items early can skip story beats. In co-op the item facts sync to your partner.</color>");
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("Camera / HUD");
            Plugin.CfgNoPause.Value = Tog(Plugin.CfgNoPause.Value, "Don't pause the game on Esc (world keeps running)");
            fovOverride = Tog(fovOverride, "Override field of view");
            fov = Slider("FOV", fov, 30f, 120f, "0");
            devHud = Tog(devHud, "Dev HUD line (fps, scene, day, position)");
            GUILayout.EndVertical();
        }

        void TabStory()
        {
            vgEventManager em = vgEventManager.Instance;
            GUILayout.BeginVertical(sBox);
            if (em == null) { L("Story systems not loaded (main menu)."); GUILayout.EndVertical(); return; }
            string task = "";
            try { if (vgQuestManager.Instance != null) vgQuestManager.Instance.GetCurrentTaskText(out task); } catch { }
            L(string.Format("<b>Day</b> {0}   <b>Time</b> {1:0.00}   <b>Task</b> {2}", em.GetCurrentDayValue(),
                em.GetFactValue("CurrentTime", null, null, vgBlackboardLocationHint.Global), Escape(task)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Trigger event", sLabel, GUILayout.Width(90));
            eventField = Field(eventField, 360);
            if (Btn("Fire", GUILayout.Width(70)) && eventField.Trim().Length > 0) { em.TriggerEvent(eventField.Trim()); R.Notice("Fired event " + eventField.Trim()); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Set fact", sLabel, GUILayout.Width(90));
            factEditName = Field(factEditName, 260);
            factEditValue = Field(factEditValue, 90);
            if (Btn("Set", GUILayout.Width(70))) SetFact(factEditName, factEditValue);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            // facts
            GUILayout.BeginVertical(sBox, GUILayout.Width(460));
            GUILayout.BeginHorizontal(); GUILayout.Label("Facts", sHeader, GUILayout.Width(80)); factSearch = Field(factSearch, 300); GUILayout.EndHorizontal();
            scroll3 = GUILayout.BeginScrollView(scroll3, GUILayout.Height(330));
            DrawFacts(GameApi.DayBoard, "day");
            DrawFacts(GameApi.GlobalBoard, "global");
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            // event log
            GUILayout.BeginVertical(sBox);
            H("Live event log");
            scroll6 = GUILayout.BeginScrollView(scroll6, GUILayout.Height(330));
            lock (logLock) for (int i = eventLog.Count - 1; i >= 0 && i > eventLog.Count - 120; i--) GUILayout.Label(eventLog[i], sSmall);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        void DrawFacts(vgBlackboard b, string tag)
        {
            if (b == null || b.Facts == null) return;
            string q = factSearch.Trim().ToLowerInvariant();
            int shown = 0;
            for (int i = 0; i < b.Facts.Count && shown < 300; i++)
            {
                vgBlackboardFact f = b.Facts[i];
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                if (q.Length > 0 && !f.name.ToLowerInvariant().Contains(q)) continue;
                shown++;
                GUILayout.BeginHorizontal();
                GUILayout.Label("<color=#888>" + tag + "</color> " + Escape(f.name), sSmall, GUILayout.Width(320));
                GUILayout.Label(f.value.ToString("0.###"), sLabel, GUILayout.Width(50));
                if (GUILayout.Button("edit", sBtn, GUILayout.Width(44))) { factEditName = f.name; factEditValue = f.value.ToString(CultureInfo.InvariantCulture); }
                GUILayout.EndHorizontal();
            }
        }

        void SetFact(string name, string val)
        {
            float v;
            if (vgEventManager.Instance == null || string.IsNullOrEmpty(name) || !float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { R.Notice("Bad fact/value"); return; }
            vgEventManager.Instance.SetFact(name.Trim(), vgFactOperation.Set, v, vgBlackboardLocationHint.Global);
            R.Notice("Fact " + name + " = " + v);
        }

        void TabEsp()
        {
            GUILayout.BeginVertical(sBox);
            H("ESP / wallhack overlay");
            espPartner = Tog(espPartner, "Partner box + name + distance (through walls)");
            espSkeleton = Tog(espSkeleton, "Partner skeleton (bones through walls)");
            espArrow = Tog(espArrow, "Edge arrow when partner is off-screen");
            espPickups = Tog(espPickups, "Pickups (things you can carry)");
            espInteract = Tog(espInteract, "Interactables (doors, notes, switches...)");
            espTeleports = Tog(espTeleports, "Developer teleport points");
            espRange = Slider("Range", espRange, 10f, 1000f, "0 m");
            L(string.Format("Tracked: {0} pickups, {1} interactables, {2} teleport points (refresh every 2 s)", pickups.Count, interacts.Count, teleports.Count));
            if (R.Avatar != null)
            {
                bool lbl = Tog(Plugin.CfgShowNameTag.Value, "Floating name tag over partner");
                if (lbl != Plugin.CfgShowNameTag.Value) { Plugin.CfgShowNameTag.Value = lbl; }
                R.Avatar.ShowLabel(Plugin.CfgShowNameTag.Value);
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(sBox);
            H("ESP colors");
            ColorRow("Partner box", ref colPartner, cfgColPartner);
            ColorRow("Skeleton", ref colSkeleton, cfgColSkeleton);
            ColorRow("Pickups", ref colPickups, cfgColPickups);
            ColorRow("Interactables", ref colInteract, cfgColInteract);
            ColorRow("Teleport points", ref colTeleports, cfgColTeleports);
            GUILayout.EndVertical();
        }

        void ColorRow(string label, ref Color col, BepInEx.Configuration.ConfigEntry<string> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, sLabel, GUILayout.Width(110));
            Color old = GUI.color;
            GUI.color = col;
            GUILayout.Box(texWhite, GUIStyle.none, GUILayout.Width(26), GUILayout.Height(20));
            GUI.color = old;
            for (int i = 0; i < Swatches.Length; i++)
            {
                GUI.color = Swatches[i];
                if (GUILayout.Button(texWhite, GUIStyle.none, GUILayout.Width(20), GUILayout.Height(20))) { col = Swatches[i]; entry.Value = ColorUtility.ToHtmlStringRGB(col); }
                GUI.color = old;
                GUILayout.Space(2);
            }
            GUILayout.Space(8);
            if (Btn(colorEditing == label ? "Done" : "Custom", GUILayout.Width(70))) colorEditing = colorEditing == label ? "" : label;
            GUILayout.EndHorizontal();
            if (colorEditing == label)
            {
                float r = Slider("   R", col.r, 0f, 1f, "0.00"), g = Slider("   G", col.g, 0f, 1f, "0.00"), b = Slider("   B", col.b, 0f, 1f, "0.00");
                Color n = new Color(r, g, b);
                if (n != col) { col = n; entry.Value = ColorUtility.ToHtmlStringRGB(col); }
            }
        }

        static Color Hex(string h)
        {
            Color c;
            return ColorUtility.TryParseHtmlString("#" + (h ?? "").TrimStart('#'), out c) ? c : Color.white;
        }

        void TabConsole()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<color=#ff8c2e><b>" + Plugin.Credit + "</b></color>", sLabel, GUILayout.Width(180));
            logUnity = Tog(logUnity, "Capture Unity log");
            if (Btn("Clear", GUILayout.Width(70))) lock (logLock) logLines.Clear();
            if (Btn("Open log folder", GUILayout.Width(130))) Application.OpenURL("file://" + Paths.BepInExRootPath);
            GUILayout.EndHorizontal();
            scroll5 = GUILayout.BeginScrollView(scroll5, sBox, GUILayout.Height(420));
            lock (logLock) for (int i = Math.Max(0, logLines.Count - 200); i < logLines.Count; i++) GUILayout.Label(logLines[i], sSmall);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("StoryCoopConsole");
            consoleInput = GUILayout.TextField(consoleInput, sField, GUILayout.Width(640));
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && GUI.GetNameOfFocusedControl() == "StoryCoopConsole";
            if (Btn("Run", GUILayout.Width(80)) || enter)
            {
                RunCommand(consoleInput);
                consoleInput = "";
                scroll5.y = float.MaxValue;
                if (enter) Event.current.Use();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Type 'help'.", sSmall);
        }

        // ================================================================= console commands

        void RunCommand(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return;
            AddLog("<color=#ff8c2e>> " + Escape(line) + "</color>");
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = a[0].ToLowerInvariant();
            vgPlayerController pc = GameApi.LocalPlayer;
            try
            {
                switch (cmd)
                {
                    case "help":
                        AddLog("tp x y z | tpp (to partner) | bring | pos | speed n | noclip | flyspeed n | timescale n | time h | nextday");
                        AddLog("event Name | fact Name [value] | facts filter | give flashlight|rope|camera|pulaski|receiver|cavekey|all");
                        AddLog("fov n | esp | hud | scene | resync | push | say text | save name | goto name | clear");
                        break;
                    case "tp":
                        if (a.Length >= 4) Teleport(new Vector3(F(a[1]), F(a[2]), F(a[3])), pc != null ? pc.transform.eulerAngles.y : 0f);
                        break;
                    case "tpp": R.GoToPartner(); break;
                    case "bring": R.BringPartner(); break;
                    case "pos":
                        if (pc != null) AddLog(string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.00} {2:0.00} {3:0.00} yaw {4:0}", GameApi.SceneName, pc.transform.position.x, pc.transform.position.y, pc.transform.position.z, pc.transform.eulerAngles.y));
                        break;
                    case "speed": speedMult = Mathf.Clamp(F(a[1]), 0.1f, 50f); AddLog("speed x" + speedMult); break;
                    case "noclip": case "fly": noclip = !noclip; break;
                    case "flyspeed": noclipSpeed = F(a[1]); break;
                    case "timescale": timeScale = Mathf.Clamp(F(a[1]), 0.05f, 10f); Time.timeScale = timeScale; break;
                    case "time": SetTimeOfDay(Mathf.Clamp((int)F(a[1]), 1, 23)); break;
                    case "nextday": NextDay(); break;
                    case "event":
                        if (vgEventManager.Instance != null) AddLog("TriggerEvent -> " + vgEventManager.Instance.TriggerEvent(line.Substring(6).Trim()));
                        break;
                    case "fact":
                        if (a.Length >= 3) SetFact(a[1], a[2]);
                        else if (vgEventManager.Instance != null) AddLog(a[1] + " = " + vgEventManager.Instance.GetFactValue(a[1], null, null, vgBlackboardLocationHint.None));
                        break;
                    case "facts":
                        {
                            string q = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                            int n = 0;
                            foreach (vgBlackboard b in new[] { GameApi.DayBoard, GameApi.GlobalBoard })
                                if (b != null) foreach (vgBlackboardFact f in b.Facts)
                                        if (f != null && f.name != null && f.name.ToLowerInvariant().Contains(q) && n++ < 60) AddLog("  " + f.name + " = " + f.value);
                            AddLog(n + " match(es)");
                        }
                        break;
                    case "give":
                        {
                            string w = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                            if (w == "flashlight" || w == "all") GiveItem("EnableFlashlight", "flashlight");
                            if (w == "rope" || w == "all") GiveItem("OnEnablePlayerRope", "rope");
                            if (w == "camera" || w == "all") GiveItem("OnEnablePlayerCamera", "camera");
                            if (w == "pulaski" || w == "all") GiveItem("OnEnablePlayerPulaski", "pulaski");
                            if (w == "receiver" || w == "all") GiveItem("OnEnablePlayerWaveReceiver", "wave receiver");
                            if (w == "cavekey" || w == "all") GiveItem("OnGetCaveKey", "cave key");
                        }
                        break;
                    case "fov": fovOverride = true; fov = F(a[1]); break;
                    case "esp": espPickups = espInteract = !espPickups; AddLog("ESP " + (espPickups ? "on" : "off")); break;
                    case "hud": devHud = !devHud; break;
                    case "scene": AddLog("Scene " + GameApi.SceneName + ", loaded scenes: " + UnityEngine.SceneManagement.SceneManager.sceneCount); break;
                    case "resync": R.RequestSnapshot("console"); break;
                    case "push": R.QueueSnapshot("console"); break;
                    case "say": R.SendNotice(R.NameField + ": " + line.Substring(4)); AddLog("sent"); break;
                    case "save": if (pc != null) SavePosition(a.Length > 1 ? a[1] : "spot", pc.transform.position, pc.transform.eulerAngles.y); break;
                    case "goto":
                        foreach (string[] s in savedPositions)
                            if (s[0] == GameApi.SceneName && a.Length > 1 && s[1] == a[1]) { Teleport(new Vector3(F(s[2]), F(s[3]), F(s[4])), F(s[5])); break; }
                        break;
                    case "clear": lock (logLock) logLines.Clear(); break;
                    default: AddLog("Unknown command. Type help."); break;
                }
            }
            catch (Exception e) { AddLog("<color=#ff6b6b>" + Escape(e.Message) + "</color>"); }
        }

        static float F(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        // ================================================================= positions file

        void SavePosition(string name, Vector3 p, float yaw)
        {
            name = (name ?? "spot").Replace("|", "/");
            savedPositions.RemoveAll(s => s[0] == GameApi.SceneName && s[1] == name);
            savedPositions.Add(new[] { GameApi.SceneName, name, p.x.ToString("0.00", CultureInfo.InvariantCulture), p.y.ToString("0.00", CultureInfo.InvariantCulture), p.z.ToString("0.00", CultureInfo.InvariantCulture), yaw.ToString("0", CultureInfo.InvariantCulture) });
            WritePositions();
            R.Notice("Saved position '" + name + "'");
        }

        void LoadPositions()
        {
            try
            {
                if (!File.Exists(posFile)) return;
                foreach (string l in File.ReadAllLines(posFile))
                {
                    string[] s = l.Split('|');
                    if (s.Length == 6) savedPositions.Add(s);
                }
            }
            catch { }
        }

        void WritePositions()
        {
            try
            {
                var lines = new List<string>();
                foreach (string[] s in savedPositions) lines.Add(string.Join("|", s));
                File.WriteAllLines(posFile, lines.ToArray());
            }
            catch (Exception e) { Plugin.Log("Positions write failed: " + e.Message); }
        }
    }
}
