using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCoop
{
    /// <summary>Everything that touches Firewatch internals lives here.</summary>
    public static class GameApi
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly FieldInfo fDestroyed = typeof(vgSaveManager).GetField("saveableObjectsDestroyed", Any);
        static readonly MethodInfo mLoadData = typeof(vgSaveManager).GetMethod("Load", Any, null, new[] { typeof(vgSaveData) }, null);
        static readonly FieldInfo fDayBoard = typeof(vgEventManager).GetField("currentDayBlackboard", Any);
        static readonly FieldInfo fDialogPool = typeof(vgDialogTreeManager).GetField("activeDialogPool", Any);
        static readonly MethodInfo mClearTree = typeof(vgDialogTreeManager).GetMethod("ClearDialogTree", Any);

        public const string ClientSlotName = "StoryCoop_Client";

        public static string SceneName
        {
            get { return SceneManager.GetActiveScene().name; }
        }

        public static bool InMainMenu { get { return SceneName == "Main_Menu"; } }

        public static vgPlayerController LocalPlayer
        {
            get
            {
                GameObject go = vgPlayerController.playerGameObject;
                return go == null ? null : go.GetComponent<vgPlayerController>();
            }
        }

        /// <summary>True when the world is loaded, the player exists and nothing is mid-load.</summary>
        public static bool WorldStable
        {
            get
            {
                vgSaveManager sm = vgSaveManager.Instance;
                if (sm == null || sm.loadingActive || sm.IsInDayTransition()) return false;
                if (vgLoadManager.Instance != null && !vgLoadManager.Instance.doneLoading) return false;
                vgPlayerController pc = LocalPlayer;
                return pc != null && pc.playerSpawned;
            }
        }

        public static bool Loading
        {
            get
            {
                vgSaveManager sm = vgSaveManager.Instance;
                if (sm != null && sm.loadingActive) return true;
                return vgLoadManager.Instance != null && !vgLoadManager.Instance.doneLoading;
            }
        }

        // ---------------------------------------------------------------- snapshots

        /// <summary>Builds the same vgSaveData the game writes to disk, without touching disk.</summary>
        public static byte[] BuildSnapshot(out string info)
        {
            vgSaveManager sm = vgSaveManager.Instance;
            var data = new vgSaveData();
            data.displayName = "StoryCoop";
            data.imagePath = "";
            data.nameSerializedDataMap = new Dictionary<string, byte[]>();
            data.loadedChunkNames = new List<string>();
            var destroyed = fDestroyed != null ? fDestroyed.GetValue(sm) as List<string> : null;
            data.destroyedObjects = destroyed != null ? new List<string>(destroyed) : new List<string>();
            data.loadedLevel = Application.loadedLevelName;

            int saved = 0, failed = 0;
            List<vgSaveable> items = sm.allSaveableItems;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;
                try { items[i].OnSave(data); saved++; }
                catch (Exception e) { failed++; Plugin.Log("Saveable " + items[i].name + " failed: " + e.Message); }
            }
            for (int j = 0; j < SECTR_Sector.All.Count; j++)
            {
                SECTR_Chunk chunk = SECTR_Sector.All[j].GetComponent<SECTR_Chunk>();
                if (chunk != null && (chunk.IsLoaded() || chunk.IsLoading()))
                    data.loadedChunkNames.Add(chunk.gameObject.name);
            }

            byte[] raw;
            using (var ms = new MemoryStream())
            {
                vgSaveManager.formatter.Serialize(ms, data);
                raw = ms.ToArray();
            }
            byte[] packed = Gzip(raw);
            info = string.Format("level={0} saveables={1} failed={2} chunks={3} raw={4}KB gz={5}KB",
                data.loadedLevel, saved, failed, data.loadedChunkNames.Count, raw.Length / 1024, packed.Length / 1024);
            return packed;
        }

        /// <summary>Loads a host snapshot exactly like Continue/Load does (full level reload).</summary>
        public static void ApplySnapshot(byte[] packed)
        {
            vgSaveManager sm = vgSaveManager.Instance;
            if (sm == null) throw new Exception("vgSaveManager missing");
            if (mLoadData == null) throw new Exception("vgSaveManager.Load(vgSaveData) not found (game version?)");
            vgSaveData data;
            using (var ms = new MemoryStream(Gunzip(packed)))
                data = (vgSaveData)vgSaveManager.formatter.Deserialize(ms);

            EnsureClientSlot();
            sm.UnloadEndGameSave();
            sm.isEnteringNewGameScreen = false;
            sm.waitingOnSaveSlotSelection = false;
            Time.timeScale = 1f;
            Plugin.Log("Applying host snapshot: level=" + data.loadedLevel + " chunks=" + data.loadedChunkNames.Count);
            mLoadData.Invoke(sm, new object[] { data });
        }

        /// <summary>The client always plays in its own slot so checkpoints never pop the slot picker.</summary>
        public static void EnsureClientSlot()
        {
            vgSaveManager sm = vgSaveManager.Instance;
            if (sm == null) return;
            if (sm.loadedSaveGame != null && sm.loadedSaveGame.saveName == ClientSlotName) return;
            vgSaveGameHandle handle = null;
            for (int i = 0; i < sm.allSaveSlots.Count; i++)
                if (sm.allSaveSlots[i] != null && sm.allSaveSlots[i].saveName == ClientSlotName) handle = sm.allSaveSlots[i];
            if (handle == null) handle = sm.CreateNamedSave(ClientSlotName);
            handle.displayName = "Co-op (client)";
            handle.userSaveFile.lastSaveTime = DateTime.Now;
            sm.loadedSaveGame = handle;
        }

        /// <summary>Client mirrors the host pressing New Game.</summary>
        public static bool StartNewGameAsClient()
        {
            vgSaveManager sm = vgSaveManager.Instance;
            if (sm == null || !InMainMenu) return false;
            EnsureClientSlot();
            sm.newSaveGameType = vgSaveData.SaveSlotType.Game;
            sm.isEnteringNewGameScreen = false;
            sm.waitingOnSaveSlotSelection = false;
            sm.UnloadEndGameSave();
            PlayMakerFSM.BroadcastEvent("NewGame");
            return true;
        }

        static byte[] Gzip(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        static byte[] Gunzip(byte[] packed)
        {
            using (var src = new MemoryStream(packed))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                return dst.ToArray();
            }
        }

        // ---------------------------------------------------------------- facts

        public const byte BoardGlobal = 0;
        public const byte BoardDay = 1;

        public static vgBlackboard GlobalBoard
        {
            get { return vgEventManager.Instance == null ? null : vgEventManager.Instance.GetBlackboardForEndGame(); }
        }

        public static vgBlackboard DayBoard
        {
            get
            {
                if (vgEventManager.Instance == null || fDayBoard == null) return null;
                return fDayBoard.GetValue(vgEventManager.Instance) as vgBlackboard;
            }
        }

        public static void ApplyFact(byte board, string name, float value)
        {
            vgEventManager em = vgEventManager.Instance;
            if (em == null) return;
            em.SetFact(name, vgFactOperation.Set, value,
                board == BoardDay ? vgBlackboardLocationHint.Day : vgBlackboardLocationHint.Global);
        }

        // ---------------------------------------------------------------- dialog

        public static IList DialogPool
        {
            get
            {
                if (vgDialogTreeManager.Instance == null || fDialogPool == null) return null;
                return fDialogPool.GetValue(vgDialogTreeManager.Instance) as IList;
            }
        }

        /// <summary>Selects and confirms the same choice locally. Returns false if no matching dialog is open.</summary>
        public static bool ApplyDialogChoice(string eventDataName, int index, string eventResponse)
        {
            IList pool = DialogPool;
            if (pool == null) return false;
            for (int i = 0; i < pool.Count; i++)
            {
                var inst = pool[i] as vgDialogInstance;
                if (inst == null || inst.dialog == null) continue;
                List<vgDialogChoice> choices = inst.dialog.Choices;
                int pick = -1;
                if (index >= 0 && index < choices.Count && choices[index].EventResponse == eventResponse) pick = index;
                if (pick < 0)
                    for (int c = 0; c < choices.Count; c++)
                        if (choices[c].EventResponse == eventResponse) { pick = c; break; }
                if (pick < 0) continue;
                if (!string.IsNullOrEmpty(eventDataName) && !string.IsNullOrEmpty(inst.dialog.eventDataName)
                    && inst.dialog.eventDataName != eventDataName) continue;

                inst.currentSelection = pick;
                inst.ConfirmChoice();
                if (inst.dialog != null && (inst.dialog.isRadioCallDialog || inst.dialog.isImportant) && mClearTree != null)
                    mClearTree.Invoke(vgDialogTreeManager.Instance, new object[] { inst });
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- player

        public static Transform FindModel(GameObject playerRoot)
        {
            if (playerRoot == null) return null;
            Transform t = playerRoot.transform.Find("henry");
            if (t != null) return t;
            foreach (Transform c in playerRoot.GetComponentsInChildren<Transform>(true))
                if (c.name == "henry") return c;
            foreach (Animator a in playerRoot.GetComponentsInChildren<Animator>(true))
                if (a.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return a.transform;
            return null;
        }

        public static void Teleport(Vector3 pos, float yaw)
        {
            vgPlayerController pc = LocalPlayer;
            if (pc == null) return;
            vgPlayerNavigationController nav = pc.GetComponent<vgPlayerNavigationController>();
            if (nav != null) nav.Teleport(pos, false);
            else pc.transform.position = pos;
            pc.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }
}
