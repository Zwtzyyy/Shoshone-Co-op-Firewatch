using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Threading.Tasks;

namespace ShoshoneCoop
{
    public class CustomNetworker : NetworkManager
    {
        public override void OnServerAddPlayer(NetworkConnection conn, short playerControllerId)
        {
            if (playerPrefab.GetComponent<NetworkedPlayer>() == null)
            {
                playerPrefab.AddComponent<NetworkedPlayer>();
            }
            var player = (GameObject)GameObject.Instantiate(playerPrefab, playerPrefab.transform.position, Quaternion.identity);
            player.gameObject.name = "Online Player Prefab";
            NetworkServer.AddPlayerForConnection(conn, player, playerControllerId);
            playerPrefab.SetActive(false);
        }
    }
}
