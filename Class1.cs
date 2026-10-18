using System;
using System.IO;
using BepInEx;
using TMPro;
using ShoshoneCoop;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShoshoneCoop
{
    // Token: 0x02000002 RID: 2
    [BepInPlugin(pluginGuid, pluginName, pluginVersion)]
    public class Class1 : BaseUnityPlugin
    {
        public void Awake()
        {
            Class1.manager = new GameObject("ShoshoneCoop Manager -- DO NOT DELETE");
            this.netManager = Class1.manager.AddComponent<NetworkManager>();
            this.netManager.onlineScene = "TeenLoop";
            this.netManager.offlineScene = "Main_Menu";
            AssetBundle assetBundle = AssetBundle.LoadFromFile(Path.Combine(Application.streamingAssetsPath, "head.head"));
            GameObject gameObject = assetBundle.LoadAsset<GameObject>("Cube");
            gameObject.AddComponent<NetworkedPlayer>();
            NetworkManager.singleton.playerPrefab = gameObject;
            NetworkManager.singleton.autoCreatePlayer = false;
            this.netManager.autoCreatePlayer = false;
            assetBundle.Unload(false);
            UnityEngine.Object.DontDestroyOnLoad(Class1.manager);
            
            Text username = gameObject.transform.Find("Nickname").GetComponentInChildren<Text>();
            if (PlayerPrefs.GetString("Username") != null)
            {
                this.username = PlayerPrefs.GetString("Username");
            }

            base.Logger.LogInfo("Initialzied");
        }

        public void Update()
        {
            if (SceneManager.GetActiveScene().name.Equals("Main_Menu"))
            {
                if (!(this.button != null))
                {
                    this.CreateButtonOnMain();
                }
                Cursor.lockState = 0;
                Cursor.visible = true;
            }
            if (SceneManager.GetActiveScene().name.Equals("TeenLoop"))
            {
                this.Player = GameObject.Find("Player Prefab").transform.Find("PlayerModel").gameObject;
                this.Player.GetComponent<vgPlayerNavigationController>().walkSpeed = 10f;
                if (Input.GetKeyDown(KeyCode.F4))
                {
                    foreach (GameObject gameObject in SceneManager.GetSceneByName("TeenLoop__SECTR DebugSectr").GetRootGameObjects())
                    {
                        base.Logger.LogInfo(gameObject.name ?? "");
                        vgDebugPlayerItems component = gameObject.transform.Find("_SECTR DebugSectr_Chunk").transform.Find("DebugPlayerItems").gameObject.GetComponent<vgDebugPlayerItems>();
                        component.giveCamera = true;
                        component.giveCaveKey = true;
                        component.giveFlashlight = true;
                        component.givePulaski = true;
                        component.giveRope = true;
                        component.giveWaveReceiver = true;
                    }
                }
                foreach (NetworkIdentity networkIdentity in UnityEngine.Object.FindObjectsOfType<NetworkIdentity>())
                {
                    if (networkIdentity.isLocalPlayer)
                    {
                        /*if (!this.GotSpine)
                        {
                            this.GotSpine = true;
                            this.Spine = networkIdentity.gameObject.transform.Find("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1").gameObject;
                            networkIdentity.transform.Find("HenryM/body").GetComponent<SkinnedMeshRenderer>().enabled = false;
                            networkIdentity.transform.Find("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1/mixamorig:Spine2/mixamorig:Neck/mixamorig:Head/Head/Firefighter_Head").GetComponent<SkinnedMeshRenderer>().enabled = false;
                            networkIdentity.transform.Find("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1/mixamorig:Spine2/mixamorig:LeftShoulder/mixamorig:LeftArm/mixamorig:LeftForeArm/mixamorig:LeftHand/RadioBase").GetComponent<MeshRenderer>().enabled = false;
                        }*/
                        UnityEngine.Object.Destroy(networkIdentity.GetComponent<CapsuleCollider>());
                        networkIdentity.gameObject.transform.position = Camera.main.transform.position;
                        this.Spine.transform.rotation = Quaternion.Euler(Camera.main.transform.eulerAngles.x, this.Spine.transform.rotation.eulerAngles.y, Camera.main.transform.eulerAngles.z);
                        Vector3 forward = Camera.main.transform.forward;
                        forward.y = 0f;
                        networkIdentity.transform.rotation = Quaternion.LookRotation(forward);
                    }
                }
            }
        }

        public void SetUsernameMiddle()
        {
            Debug.Log("MiddleMan");
            this.SetUsernameButton(GameObject.Find("Canvas(Clone)").transform.Find("Username").gameObject);
        }

        public void SetUsernameButton(GameObject InputFieldText)
        {
            PlayerPrefs.SetString("Username", InputFieldText.GetComponent<InputField>().text);
            PlayerPrefs.Save();
            base.Logger.LogInfo("Username set to: " + InputFieldText.GetComponent<InputField>().text);
        }

        public void CreateButtonOnMain()
        {
            base.Logger.LogInfo("Making the main menu button button.");
            Transform transform = GameObject.Find("Main Menu").transform;
            this.button = transform.transform.Find("Main Menu Group").Find("Settings Button").gameObject;
            this.MultiplayerButton = UnityEngine.Object.Instantiate<GameObject>(this.button);
            this.MultiplayerButton.transform.parent = this.button.transform.parent;
            this.MultiplayerButton.GetComponentInChildren<TextMeshProUGUI>().text = "Multiplayer";
            this.MultiplayerButton.gameObject.SetActive(false);
            this.MultiplayerButton.gameObject.SetActive(true);
            this.MultiplayerButton.GetComponent<Button>().onClick.AddListener(new UnityAction(this.OnMPButtonClick));
        }

        public void OnMPButtonClick()
        {
            base.Logger.LogInfo("MP Button Clicked!!");
            this.showUsernameField = true;
            base.Invoke("doShit", 0.5f);
        }

        private void unloadSettings()
        {
            GameObject[] rootGameObjects = SceneManager.GetSceneByName("settings").GetRootGameObjects();
            if (rootGameObjects.Length != 0)
            {
                GameObject[] array = rootGameObjects;
                for (int i = 0; i < array.Length; i++)
                {
                    UnityEngine.Object.Destroy(array[i]);
                }
                return;
            }
            base.Logger.LogInfo("nothing else in this one");
        }

        private void doShit()
        {
            Scene sceneByName = SceneManager.GetSceneByName("settings");
            UnityEngine.Object.Destroy(this.MultiplayerButton);
            foreach (GameObject gameObject in sceneByName.GetRootGameObjects())
            {
                base.Logger.LogInfo(gameObject.name ?? "");
                if (gameObject.name == "SettingsScreenManager")
                {
                    GameObject gameObject2 = gameObject.transform.Find("SafeZoner").transform.Find("Menu Header").gameObject;
                    GameObject gameObject3 = gameObject.transform.Find("SafeZoner").Find("SettingsOptionsRoot").Find("CanvasGroup").Find("SettingsVerticalList").gameObject;
                    gameObject3.transform.Find("Label Audio").gameObject.GetComponent<TextMeshProUGUI>().text = "Create & Join";
                    gameObject3.transform.Find("Master Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Music Volume Slider").gameObject.SetActive(false);
                    gameObject3.transform.Find("Voice Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Sounds Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Mute Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Subtitle Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Subtitle Checkbox").transform.Find("ToggleContainer").transform.Find("TooltipText").GetComponent<TextMeshProUGUI>().text = "";
                    gameObject3.transform.Find("Spacer (1)").gameObject.SetActive(false);
                    gameObject3.transform.Find("LanguageDropDown").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Label Gameplay").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("GameplayGrid").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer (2)").gameObject.SetActive(false);
                    gameObject3.transform.Find("Minimal Interface Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Brightness Slider").gameObject.SetActive(false);
                    gameObject2.transform.Find("UI_TabNav").gameObject.SetActive(false);
                    gameObject2.transform.Find("UI_SectionHeader").Find("SectionHeaderLabel").GetComponent<TextMeshProUGUI>().text = "Multiplayer";
                    gameObject3.transform.parent.Find("Version Label").GetComponent<TextMeshProUGUI>().text = "mp v0.0.4";
                    if (this.showUsernameField)
                    {
                        GameObject gameObject4 = UnityEngine.Object.Instantiate<GameObject>(AssetBundle.LoadFromFile(Path.Combine(Application.streamingAssetsPath, "UI.im")).LoadAsset<GameObject>("Canvas"));
                        this.IPInputField = gameObject4.transform.Find("InputField").GetComponent<InputField>();
                        this.PortInputField = gameObject4.transform.Find("Port").GetComponent<InputField>();
                        gameObject4.transform.Find("Set").GetComponent<Button>().onClick.AddListener(new UnityAction(this.SetUsernameMiddle));
                        if (this.username != null)
                        {
                            gameObject4.transform.Find("Username").gameObject.GetComponent<InputField>().text = this.username;
                        }
                    }
                    Transform transform = GameObject.Find("Main Menu").transform;
                    this.button = transform.transform.Find("Main Menu Group").Find("Settings Button").gameObject;
                    GameObject gameObject5 = UnityEngine.Object.Instantiate<GameObject>(this.button);
                    gameObject5.transform.parent = gameObject3.transform;
                    gameObject5.GetComponentInChildren<TextMeshProUGUI>().text = "Create Room";
                    gameObject5.gameObject.SetActive(false);
                    gameObject5.gameObject.SetActive(true);
                    gameObject5.GetComponent<Button>().onClick.RemoveAllListeners();
                    gameObject5.GetComponent<Button>().onClick.AddListener(new UnityAction(this.CreateRoomButtonClick));
                    GameObject gameObject6 = UnityEngine.Object.Instantiate<GameObject>(this.button);
                    gameObject6.transform.parent = gameObject3.transform;
                    gameObject6.GetComponentInChildren<TextMeshProUGUI>().text = "Join Room";
                    gameObject6.gameObject.SetActive(false);
                    gameObject6.gameObject.SetActive(true);
                    gameObject6.GetComponent<Button>().onClick.RemoveAllListeners();
                    gameObject6.GetComponent<Button>().onClick.AddListener(new UnityAction(this.JoinRoomButtonClick));
                    GameObject gameObject7 = UnityEngine.Object.Instantiate<GameObject>(this.button);
                    gameObject7.transform.parent = gameObject3.transform;
                    gameObject7.GetComponentInChildren<TextMeshProUGUI>().text = "Preferences";
                    gameObject7.gameObject.SetActive(false);
                    gameObject7.gameObject.SetActive(true);
                    gameObject7.GetComponent<Button>().onClick.RemoveAllListeners();
                    gameObject7.GetComponent<Button>().onClick.AddListener(new UnityAction(this.PreferencesButtonClick));
                    GameObject.Find("SettingsScreenManager/SafeZoner/Menu Footer/UI_ButtonBack").GetComponent<Button>().onClick.AddListener(new UnityAction(this.CreateButtonOnMain));
                }
            }
        }

        public void JoinRoomButtonClick()
        {
            PlayMakerFSM.BroadcastEvent("NewFreeRoamGame");
            NetworkManager.singleton.networkAddress = this.IPInputField.textComponent.text;
            if (this.PortInputField.text != null)
            {
                NetworkManager.singleton.networkPort = int.Parse(this.PortInputField.text);
                base.Logger.LogInfo("Started Host On " + this.IPInputField.textComponent.text + int.Parse(this.PortInputField.text).ToString());
            }
            else
            {
                base.Logger.LogInfo("Started Host On " + this.IPInputField.textComponent.text);
            }
            this.netManager.StartClient();
            base.Logger.LogInfo("Attempting Connection to " + this.IPInputField.textComponent.text);
            NetworkManager.singleton.autoCreatePlayer = true;
            this.netManager.autoCreatePlayer = true;
        }

        public void CreateRoomButtonClick()
        {
            PlayMakerFSM.BroadcastEvent("NewFreeRoamGame");
            NetworkManager.singleton.networkAddress = this.IPInputField.textComponent.text;
            if (this.PortInputField.text != null)
            {
                NetworkManager.singleton.networkPort = int.Parse(this.PortInputField.text);
                base.Logger.LogInfo("Started Host On " + this.IPInputField.textComponent.text + int.Parse(this.PortInputField.text).ToString());
            }
            else
            {
                base.Logger.LogInfo("Started Host On " + this.IPInputField.textComponent.text);
            }
            this.netManager.StartHost(null, 2);
            NetworkManager.singleton.autoCreatePlayer = true;
            this.netManager.autoCreatePlayer = true;
        }


        public void PreferencesButtonClick()
        {
            base.Logger.LogInfo("Preferences button clicked!");
            this.unloadSettings();
            this.unloadSettings();
            base.Invoke("preferencesTab", 0.5f);
        }

        private void preferencesTab()
        {
            foreach (GameObject gameObject in SceneManager.GetSceneByName("settings").GetRootGameObjects())
            {
                base.Logger.LogInfo(gameObject.name ?? "");
                if (gameObject.name == "SettingsScreenManager")
                {
                    GameObject gameObject2 = gameObject.transform.Find("SafeZoner").transform.Find("Menu Header").gameObject;
                    GameObject gameObject3 = gameObject.transform.Find("SafeZoner").Find("SettingsOptionsRoot").Find("CanvasGroup").Find("SettingsVerticalList").gameObject;
                    gameObject3.transform.Find("Label Audio").gameObject.GetComponent<TextMeshProUGUI>().text = "Mod Options";
                    gameObject3.transform.Find("Master Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Music Volume Slider").gameObject.SetActive(false);
                    gameObject3.transform.Find("Voice Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Sounds Volume").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Mute Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Subtitle Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Subtitle Checkbox").transform.Find("ToggleContainer").transform.Find("TooltipText").GetComponent<TextMeshProUGUI>().text = "";
                    gameObject3.transform.Find("Spacer (1)").gameObject.SetActive(false);
                    gameObject3.transform.Find("LanguageDropDown").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Label Gameplay").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("GameplayGrid").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer (2)").gameObject.SetActive(false);
                    gameObject3.transform.Find("Minimal Interface Checkbox").gameObject.SetActive(false);
                    gameObject3.transform.Find("Spacer").gameObject.SetActive(false);
                    gameObject3.transform.Find("Brightness Slider").gameObject.SetActive(false);
                    gameObject2.transform.Find("UI_TabNav").gameObject.SetActive(false);
                    gameObject2.transform.Find("UI_SectionHeader").Find("SectionHeaderLabel").GetComponent<TextMeshProUGUI>().text = "Multiplayer Preferences";
                    gameObject3.transform.parent.Find("Version Label").GetComponent<TextMeshProUGUI>().text = "mp v0.0.4";
                }
            }
        }
        public const string pluginGuid = "zwtzyy.firewatch.shoshonecoop";
        public const string pluginName = "Shoshone Co-op";
        public const string pluginVersion = "0.0.5";
        private GameObject button;

        // Token: 0x04000005 RID: 5
        public static GameObject manager;

        // Token: 0x04000006 RID: 6
        private NetworkManager netManager;

        // Token: 0x04000007 RID: 7
        private InputField IPInputField;

        // Token: 0x04000008 RID: 8
        private InputField PortInputField;

        // Token: 0x04000009 RID: 9
        public GameObject Player;

        // Token: 0x0400000A RID: 10
        public static Class1 Initilization;

        // Token: 0x0400000B RID: 11
        private UnityAction action;

        // Token: 0x0400000C RID: 12
        public vgSaveManager savemanager;

        // Token: 0x0400000D RID: 13
        public bool showUsernameField;

        // Token: 0x0400000E RID: 14
        private string username = "";

        // Token: 0x0400000F RID: 15
        private GUIStyle textFieldStyle;

        // Token: 0x04000010 RID: 16
        private GameObject Spine;

        // Token: 0x04000011 RID: 17
        private bool GotSpine;

        // Token: 0x04000012 RID: 18
        private GameObject MultiplayerButton;
    }
}
