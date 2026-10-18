using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ShoshoneCoop
{
    // Token: 0x02000003 RID: 3
    public class NetworkedPlayer : NetworkBehaviour
    {
        public List<Transform> localPlayerBones = new List<Transform>();
        public List<Transform> onlinePlayerBones = new List<Transform>();

        CopyTransform ct;

        void Start()
        {
            // first add the copy transform script
            if(ct == null) 
                ct = this.gameObject.AddComponent<CopyTransform>();

            GetOnlinePlayerRig();
            GetLocalPlayerRig();
        }

        private void Update()
        {
            ct.sourceTransforms = localPlayerBones;
            ct.targetTransforms = onlinePlayerBones;

            ct.Start();
            // YAYY!!! IT WORKS

            UnityEngine.Object.DontDestroyOnLoad(this);
            if (base.GetComponent<NetworkIdentity>().hasAuthority)
            {
                if (Input.GetKeyDown(KeyCode.LeftShift))
                {
                    base.GetComponent<Animator>().Play("UseRadio");
                }
                if (Input.GetKeyUp(KeyCode.LeftShift))
                {
                    base.GetComponent<Animator>().Play("UseRadioBack");
                }
                if (Input.GetKeyDown(KeyCode.Mouse1))
                {
                    base.transform.FindChild("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1/mixamorig:Spine2/mixamorig:Neck/mixamorig:Head/Head").GetComponent<Animator>().Play("Zoom");
                }
                if (Input.GetKeyUp(KeyCode.Mouse1))
                {
                    base.transform.FindChild("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1/mixamorig:Spine2/mixamorig:Neck/mixamorig:Head/Head").GetComponent<Animator>().Play("Idle");
                }
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D))
                {
                    this.Walking = true;
                }
                else
                {
                    this.Walking = false;
                }
                if (this.Walking && !this.usingAnim1)
                {
                    base.GetComponent<Animator>().Play("walk", 1);
                    base.GetComponent<NetworkAnimator>().SetTrigger("Walk");
                    this.usingAnim1 = true;
                }
                if (!this.Walking && this.usingAnim1)
                {
                    base.GetComponent<Animator>().Play("Idle", 1);
                    base.GetComponent<NetworkAnimator>().SetTrigger("WalkToIdle");
                    this.usingAnim1 = false;
                    return;
                }
            }
            else
            {
                UnityEngine.Object.Destroy(base.transform.FindChild("HenryM/Armature/mixamorig:Hips/mixamorig:Spine/mixamorig:Spine1/mixamorig:Spine2/mixamorig:Neck/mixamorig:Head/Head").GetComponent<Animator>());
            }
        }

        void GetOnlinePlayerRig()
        {
            Transform armature = this.transform.Find("henry").Find("henryroot");
            this.transform.Find("henry").Find("body").gameObject.SetActive(false);
            if (armature != null)
            {
                onlinePlayerBones.Clear(); // Clear the list to avoid duplicates
                CollectOnlineChildTransforms(armature);
            }
            else
            {
                Debug.LogError("henry or henryroot transform not found.");
            }
        }

        void CollectOnlineChildTransforms(Transform parent)
        {
            // Add the current transform first
            onlinePlayerBones.Add(parent);

            // Recursively add all child transforms
            foreach (Transform child in parent)
            {
                CollectOnlineChildTransforms(child);
            }
        }

        void CollectLocalChildTransforms(Transform parent)
        {
            // Add the current transform first
            localPlayerBones.Add(parent);

            // Recursively add all child transforms
            foreach (Transform child in parent)
            {
                CollectLocalChildTransforms(child);
            }
        }

        void GetLocalPlayerRig()
        {
            // get local player
            Transform model = GameObject.Find("Player Prefab").transform.Find("PlayerModel");

            Transform armature = model.Find("henry").Find("henryroot");
            if (armature != null)
            {
                localPlayerBones.Clear(); // Clear the list to avoid duplicates
                CollectLocalChildTransforms(armature);
            }
            else
            {
                Debug.LogError("henry or henryroot transform not found.");
            }
        }

        // Token: 0x04000013 RID: 19
        [SyncVar]
        public string syncUsername;

        // Token: 0x04000014 RID: 20
        private bool Walking;

        // Token: 0x04000015 RID: 21
        private bool usingAnim1;
    }
}
