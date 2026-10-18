using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace StoryCoop
{
    public enum PeerMode { Offline, Host, Client }

    public static class Msg
    {
        public const byte Hello = 1;          // client->host: proto, name
        public const byte Welcome = 2;        // host->client: proto, name
        public const byte PlayerState = 3;    // both
        public const byte Facts = 4;          // both: list of (board, name, value)
        public const byte Snapshot = 5;       // host->client: gzip(BinaryFormatter(vgSaveData))
        public const byte NewGame = 6;        // host->client
        public const byte Dialog = 7;         // both
        public const byte RequestSnapshot = 8;// client->host
        public const byte Ping = 9;
        public const byte Bye = 10;
        public const byte TeleportTo = 11;    // both: "come to me" (pos, yaw, scene)
        public const byte Notice = 12;        // both: text shown in overlay
        public const int Proto = 2;
    }

    /// <summary>Single-peer TCP link. Host keeps accepting so a client can reconnect.</summary>
    public class NetPeer
    {
        public volatile PeerMode Mode = PeerMode.Offline;
        public volatile bool Connected;
        public volatile string LastError = "";

        private readonly Queue<byte[]> inbox = new Queue<byte[]>();
        private readonly Queue<byte[]> outbox = new Queue<byte[]>();
        private readonly object inLock = new object();
        private readonly object outLock = new object();
        private readonly AutoResetEvent outSignal = new AutoResetEvent(false);

        private TcpListener listener;
        private TcpClient client;
        private NetworkStream stream;
        private volatile bool running;
        private int generation;

        public bool StartHost(int port)
        {
            Shutdown();
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                running = true;
                Mode = PeerMode.Host;
                LastError = "";
                int gen = ++generation;
                StartThread(() => AcceptLoop(gen), "StoryCoop-Accept");
                StartThread(() => SendLoop(gen), "StoryCoop-Send");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Shutdown();
                return false;
            }
        }

        public bool StartClient(string host, int port)
        {
            Shutdown();
            try
            {
                var c = new TcpClient();
                c.NoDelay = true;
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000, false))
                {
                    try { c.Close(); } catch { }
                    LastError = "Connect timed out (is the host listening? firewall?)";
                    return false;
                }
                c.EndConnect(ar);
                running = true;
                Mode = PeerMode.Client;
                int gen = ++generation;
                Attach(c, gen);
                StartThread(() => SendLoop(gen), "StoryCoop-Send");
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Shutdown();
                return false;
            }
        }

        private void Attach(TcpClient c, int gen)
        {
            c.NoDelay = true;
            c.SendBufferSize = 1 << 20;
            c.ReceiveBufferSize = 1 << 20;
            client = c;
            stream = c.GetStream();
            lock (outLock) outbox.Clear();
            Connected = true;
            NetworkStream s = stream;
            StartThread(() => ReadLoop(s, gen), "StoryCoop-Read");
        }

        private static void StartThread(ThreadStart fn, string name)
        {
            var t = new Thread(fn);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        private void AcceptLoop(int gen)
        {
            while (running && gen == generation)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    if (!running || gen != generation) { c.Close(); return; }
                    if (Connected)
                    {
                        // only one partner at a time
                        c.Close();
                        continue;
                    }
                    Attach(c, gen);
                }
                catch (Exception e)
                {
                    if (running && gen == generation) LastError = e.Message;
                    Thread.Sleep(200);
                }
            }
        }

        private void ReadLoop(NetworkStream s, int gen)
        {
            byte[] hdr = new byte[4];
            try
            {
                while (running && gen == generation)
                {
                    if (!ReadFull(s, hdr, 4)) break;
                    int len = BitConverter.ToInt32(hdr, 0);
                    if (len <= 0 || len > 256 * 1024 * 1024) { LastError = "Bad frame length " + len; break; }
                    byte[] body = new byte[len];
                    if (!ReadFull(s, body, len)) break;
                    lock (inLock) inbox.Enqueue(body);
                }
            }
            catch (Exception e)
            {
                if (running) LastError = e.Message;
            }
            if (gen == generation) DropConnection();
        }

        private static bool ReadFull(Stream s, byte[] buf, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n = s.Read(buf, off, len - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private void SendLoop(int gen)
        {
            while (running && gen == generation)
            {
                outSignal.WaitOne(250, false);
                while (true)
                {
                    byte[] frame = null;
                    lock (outLock) { if (outbox.Count > 0) frame = outbox.Dequeue(); }
                    if (frame == null) break;
                    NetworkStream s = stream;
                    if (s == null || !Connected) break;
                    try
                    {
                        byte[] hdr = BitConverter.GetBytes(frame.Length);
                        s.Write(hdr, 0, 4);
                        s.Write(frame, 0, frame.Length);
                    }
                    catch (Exception e)
                    {
                        LastError = e.Message;
                        DropConnection();
                        break;
                    }
                }
            }
        }

        private void DropConnection()
        {
            Connected = false;
            try { if (stream != null) stream.Close(); } catch { }
            try { if (client != null) client.Close(); } catch { }
            stream = null;
            client = null;
            if (Mode == PeerMode.Client) Mode = PeerMode.Offline;
        }

        /// <summary>frame = [type][payload]</summary>
        public void Send(byte type, byte[] payload)
        {
            if (!Connected) return;
            int n = payload == null ? 0 : payload.Length;
            byte[] frame = new byte[n + 1];
            frame[0] = type;
            if (n > 0) Buffer.BlockCopy(payload, 0, frame, 1, n);
            lock (outLock) outbox.Enqueue(frame);
            outSignal.Set();
        }

        public bool Poll(out byte type, out byte[] payload)
        {
            byte[] frame = null;
            lock (inLock) { if (inbox.Count > 0) frame = inbox.Dequeue(); }
            if (frame == null) { type = 0; payload = null; return false; }
            type = frame[0];
            payload = new byte[frame.Length - 1];
            Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);
            return true;
        }

        public int PendingOut { get { lock (outLock) return outbox.Count; } }

        public void Shutdown()
        {
            running = false;
            generation++;
            DropConnection();
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null;
            lock (inLock) inbox.Clear();
            lock (outLock) outbox.Clear();
            Mode = PeerMode.Offline;
        }
    }

    public static class Pack
    {
        public static byte[] Build(Action<BinaryWriter> fn)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                fn(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] data)
        {
            return new BinaryReader(new MemoryStream(data));
        }
    }
}
