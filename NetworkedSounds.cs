using System;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ShoshoneCoop
{
    // Token: 0x02000004 RID: 4
    public class NetworkedSounds : MonoBehaviour
    {
        // Token: 0x06000010 RID: 16
        [DllImport("winmm.dll")]
        private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr hwndCallback);

        // Token: 0x06000011 RID: 17 RVA: 0x00003334 File Offset: 0x00001534
        public void Play(string path, string name)
        {
            new StringBuilder(256);
            int num = NetworkedSounds.mciSendString("open \"" + path + "\" type waveaudio alias " + name, null, 0, IntPtr.Zero);
            if (num != 0)
            {
                NetworkedSounds.mciSendString("close " + name, null, 0, IntPtr.Zero);
                throw new Exception(string.Format("Failed to open audio file. Error code: {0}", num));
            }
            num = NetworkedSounds.mciSendString("play " + name, null, 0, IntPtr.Zero);
            if (num != 0)
            {
                NetworkedSounds.mciSendString("close " + name, null, 0, IntPtr.Zero);
                throw new Exception(string.Format("Failed to play audio file. Error code: {0}", num));
            }
        }

        // Token: 0x06000012 RID: 18 RVA: 0x0000212E File Offset: 0x0000032E
        public void PlayA()
        {
            this.Play(Paths.GameRootPath + "\\noot.wav", "Nooter");
        }

        // Token: 0x06000013 RID: 19 RVA: 0x0000214A File Offset: 0x0000034A
        public void OnEnable()
        {
            if (!this.played)
            {
                this.PlayA();
                this.played = true;
            }
        }

        // Token: 0x06000014 RID: 20 RVA: 0x00002161 File Offset: 0x00000361
        public void OnDisable()
        {
            this.played = false;
        }

        // Token: 0x04000016 RID: 22
        public bool played;
    }
}
