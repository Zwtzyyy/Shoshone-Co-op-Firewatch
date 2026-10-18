using System;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StoryCoop
{
    [BepInPlugin(Guid, "Firewatch Story Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "zwtzyy.firewatch.storycoop";
        public const string Version = "0.5.0";
        public const string Credit = "Mod Made By Zwtzy";

        internal static Plugin Instance;
        internal static ConfigEntry<string> CfgIp, CfgName, CfgExcludedFacts;
        internal static ConfigEntry<int> CfgPort;
        internal static ConfigEntry<float> CfgSendRate;
        internal static ConfigEntry<bool> CfgShareScripted, CfgShareAllTriggers, CfgSyncWorldObjects, CfgNoPause, CfgHeadSphere, CfgShowNameTag, CfgSyncFacts, CfgSyncDialog, CfgDialogFallback, CfgFollowNewGame, CfgClientAutoResync, CfgShowUI;
        internal static ConfigEntry<KeyCode> KeyUI, KeyBring, KeyGoto, KeyResync;

        static StreamWriter logFile;
        static readonly object logLock = new object();

        void Awake()
        {
            Instance = this;
            CfgIp = Config.Bind("Network", "Ip", "127.0.0.1", "Host address to join.");
            CfgPort = Config.Bind("Network", "Port", 7778, "TCP port (7778 so it never clashes with the old UNET mods on 7777).");
            CfgName = Config.Bind("Network", "PlayerName", "Henry", "Name shown above your head for the partner.");
            CfgSendRate = Config.Bind("Sync", "PlayerSendRate", 20f, "Position updates per second.");
            CfgSyncFacts = Config.Bind("Sync", "SyncStoryFacts", true, "Mirror story facts (global + day blackboards) both ways.");
            CfgSyncDialog = Config.Bind("Sync", "SyncDialogChoices", true, "Mirror radio/dialog choices both ways.");
            CfgDialogFallback = Config.Bind("Sync", "DialogFallbackTrigger", true, "If the partner picks a reply you don't have open, fire that reply's event anyway.");
            CfgFollowNewGame = Config.Bind("Sync", "ClientFollowsNewGame", true, "Client starts a new story when the host does.");
            CfgClientAutoResync = Config.Bind("Sync", "ClientAutoResync", true, "If the client loads a save on its own, pull the host's state again.");
            CfgExcludedFacts = Config.Bind("Sync", "ExcludedFacts", "CurrentDay,CurrentTime,LoadProgress,SpeechPlaying", "Comma list of facts never synced (day is synced by snapshot).");
            CfgShowUI = Config.Bind("UI", "ShowOverlay", false, "Open the Co-op/Dev window on start.");
            CfgShareScripted = Config.Bind("Sync", "ShareScriptedMoments", true, "When one player walks into a story trigger (Delilah calls, scripted events), it happens for both.");
            CfgShareAllTriggers = Config.Bind("Sync", "ShareAllTriggerZones", false, "Experimental: also share repeatable trigger zones, not just one-shot story triggers.");
            CfgSyncWorldObjects = Config.Bind("Sync", "SyncWorldObjects", true, "Host decides: sync loose physics objects (things you can pick up/throw). Doors, ropes and notes always sync.");
            CfgNoPause = Config.Bind("Gameplay", "DontPauseOnEsc", false, "Esc opens the pause menu but the world keeps running.");
            CfgHeadSphere = Config.Bind("Avatar", "HeadSphere", true, "Give the partner a simple head (the first-person rig has none).");
            CfgShowNameTag = Config.Bind("Avatar", "NameTag", true, "Floating name over the partner.");
            KeyUI = Config.Bind("Keys", "ToggleOverlay", KeyCode.F8, "");
            KeyBring = Config.Bind("Keys", "BringPartnerToMe", KeyCode.F9, "");
            KeyGoto = Config.Bind("Keys", "GoToPartner", KeyCode.F10, "");
            KeyResync = Config.Bind("Keys", "ResyncFromHost", KeyCode.F7, "Client: reload the host's current state.");

            try
            {
                string path = Path.Combine(Paths.BepInExRootPath, "StoryCoop_" + Process.GetCurrentProcess().Id + ".log");
                logFile = new StreamWriter(path, false);
                logFile.AutoFlush = true;
            }
            catch { }

            Application.runInBackground = true; // two instances on one PC: the unfocused one must keep simulating

            // vgSaveManager.Load destroys every GameObject not tagged MAIN (including BepInEx's manager).
            // Tagging keeps all plugins alive across loads.
            try { gameObject.tag = "MAIN"; } catch (Exception e) { Log("Could not tag BepInEx manager: " + e.Message); }

            var runnerGo = new GameObject("StoryCoop_Runner");
            DontDestroyOnLoad(runnerGo);
            try { runnerGo.tag = "MAIN"; } catch { }
            runnerGo.AddComponent<CoopRunner>();
            runnerGo.AddComponent<DevMenu>();

            try
            {
                new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
                Log("Harmony patches applied");
            }
            catch (Exception e) { Log("Harmony failed: " + e); }

            Log("Firewatch Story Co-op " + Version + " - " + Credit + " - loaded. " + KeyUI.Value + " = overlay");
        }

        internal static void Log(string msg)
        {
            if (Instance != null) Instance.Logger.LogInfo(msg);
            DevMenu.AddLog(msg.Replace("<", "‹").Replace(">", "›"));
            lock (logLock)
            {
                try { if (logFile != null) logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg); } catch { }
            }
        }
    }

    [HarmonyPatch(typeof(vgMainMenuController), "OnNewGameStart")]
    static class Patch_NewGame
    {
        static void Postfix()
        {
            if (CoopRunner.Instance != null) CoopRunner.Instance.OnLocalNewGame();
        }
    }

    [HarmonyPatch(typeof(vgEventManager), "TriggerSingleEvent")]
    static class Patch_EventLog
    {
        static void Prefix(string eventToTrigger)
        {
            if (!string.IsNullOrEmpty(eventToTrigger)) DevMenu.LogEvent(eventToTrigger);
        }
    }

    [HarmonyPatch(typeof(vgDialogInstance), "ConfirmChoice")]
    static class Patch_DialogConfirm
    {
        static void Prefix(vgDialogInstance __instance)
        {
            try
            {
                if (CoopRunner.Instance == null || CoopRunner.ApplyingRemote) return;
                vgDialogDefinition d = __instance.dialog;
                int i = __instance.currentSelection;
                if (d == null || i < 0 || i >= d.Choices.Count) return;
                CoopRunner.Instance.OnLocalDialogChoice(d.eventDataName ?? "", i, d.Choices[i].EventResponse ?? "", d.isRadioCallDialog);
            }
            catch (Exception e) { Plugin.Log("Dialog patch: " + e.Message); }
        }
    }
}
