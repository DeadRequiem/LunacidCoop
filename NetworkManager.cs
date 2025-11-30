using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace LunacidCoopMod
{
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance;

        public event Action<NetworkMessage> OnMessageReceived;
        public event Action OnConnected;
        public event Action OnDisconnected;

        public bool IsHost { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsConnected { get; private set; }
        public string ConnectedPlayerName { get; private set; }

        private TcpListener server;
        private TcpClient client;
        private NetworkStream stream;
        private Thread networkThread;
        private volatile bool running;

        private readonly MessageBuffer buffer = new MessageBuffer();
        private readonly ConcurrentQueue<NetworkMessage> incoming = new ConcurrentQueue<NetworkMessage>();
        private readonly ConcurrentQueue<NetworkMessage> outgoing = new ConcurrentQueue<NetworkMessage>();

        // Batching for high-frequency messages
        private readonly ConcurrentDictionary<string, NetworkMessage> pendingNpcUpdates = new ConcurrentDictionary<string, NetworkMessage>();
        private float lastBatchSend = 0f;
        private const float BATCH_INTERVAL = 0.05f; // Send batched updates every 50ms

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // Process incoming messages
            while (incoming.TryDequeue(out var msg))
                OnMessageReceived?.Invoke(msg);

            // Send batched NPC updates
            if (Time.time - lastBatchSend >= BATCH_INTERVAL)
            {
                SendBatchedNpcUpdates();
                lastBatchSend = Time.time;
            }
        }

        public void StartHost(int port = 7777)
        {
            if (IsConnected || IsHost) return;

            try
            {
                Application.runInBackground = true;
                server = new TcpListener(IPAddress.Any, port);
                server.Start();

                IsHost = true;
                running = true;

                networkThread = new Thread(HostAcceptAndRun) { IsBackground = true };
                networkThread.Start();

                Plugin.Log.LogInfo($"[Net] Hosting on {port}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] Host start failed: {e.Message}");
                IsHost = false;
                running = false;
            }
        }

        public void ConnectToHost(string ip, int port = 7777)
        {
            if (IsConnected || IsClient) return;

            try
            {
                Application.runInBackground = true;
                client = new TcpClient();
                client.Connect(ip, port);
                stream = client.GetStream();

                IsClient = true;
                IsConnected = true;
                running = true;

                SendMessage(new HandshakeMessage
                {
                    PlayerName = "Player",
                    ModVersion = Plugin.ModVersion
                });

                networkThread = new Thread(NetworkLoop) { IsBackground = true };
                networkThread.Start();

                OnConnected?.Invoke();
                Plugin.Log.LogInfo($"[Net] Connected to {ip}:{port}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] Connect failed: {e.Message}");
                Cleanup();
            }
        }

        public void SendMessage(NetworkMessage message)
        {
            if (!IsConnected || message == null) return;

            // Batch NPC updates instead of sending immediately
            if (message is NpcUpdateMessage npcMsg)
            {
                string key = $"{npcMsg.SceneName}_{npcMsg.NpcId}";
                pendingNpcUpdates.AddOrUpdate(key, npcMsg, (k, existing) => npcMsg);
                Plugin.Log.LogDebug($"[Queue Batch] NPC {npcMsg.NpcId} HP={npcMsg.HP}");
                return;
            }

            // Send other messages immediately
            Plugin.Log.LogDebug($"[Queue] {message.Kind}");
            outgoing.Enqueue(message);
        }

        private void SendBatchedNpcUpdates()
        {
            if (pendingNpcUpdates.IsEmpty) return;

            var updates = new List<NetworkMessage>();
            foreach (var kvp in pendingNpcUpdates)
            {
                updates.Add(kvp.Value);
            }
            pendingNpcUpdates.Clear();

            foreach (var update in updates)
            {
                outgoing.Enqueue(update);
            }

            if (updates.Count > 0)
                Plugin.Log.LogDebug($"[Batch Send] {updates.Count} NPC updates");
        }

        public void Disconnect()
        {
            running = false;
            Cleanup();
            OnDisconnected?.Invoke();
            Plugin.Log.LogInfo("[Net] Disconnected");
        }

        private void HostAcceptAndRun()
        {
            try
            {
                Plugin.Log.LogInfo("[Net] Waiting for client…");
                client = server.AcceptTcpClient();
                stream = client.GetStream();
                IsConnected = true;
                Plugin.Log.LogInfo("[Net] Client connected");

                UnityMain(() => OnConnected?.Invoke());

                SendMessage(new HandshakeMessage
                {
                    PlayerName = "Host",
                    ModVersion = Plugin.ModVersion
                });

                NetworkLoop();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] Host accept/run error: {e.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private void NetworkLoop()
        {
            var tmp = new byte[8192]; // Increased buffer size
            int consecutiveErrors = 0;
            const int MAX_CONSECUTIVE_ERRORS = 10;

            while (running)
            {
                try
                {
                    // Send outgoing messages (with throttling)
                    int sent = 0;
                    while (outgoing.TryDequeue(out var msg) && sent < 20) // Limit messages per loop
                    {
                        var packet = NetworkMessageHandler.Serialize(msg);
                        if (packet != null && stream != null)
                        {
                            Plugin.Log.LogDebug($"[Send] {msg.Kind}");
                            stream.Write(packet, 0, packet.Length);
                            sent++;
                        }
                    }

                    // Read incoming data
                    if (stream != null && stream.DataAvailable)
                    {
                        int read = stream.Read(tmp, 0, tmp.Length);
                        if (read > 0)
                        {
                            Plugin.Log.LogDebug($"[RecvBytes] {read} bytes");
                            var maybe = buffer.ProcessData(tmp, read);
                            if (maybe != null)
                            {
                                incoming.Enqueue(Preprocess(maybe));
                                consecutiveErrors = 0; // Reset error count on success
                            }

                            // Process any additional complete messages in buffer
                            while ((maybe = buffer.ProcessData(Array.Empty<byte>(), 0)) != null)
                                incoming.Enqueue(Preprocess(maybe));
                        }
                    }

                    consecutiveErrors = 0;
                }
                catch (Exception e)
                {
                    consecutiveErrors++;
                    Plugin.Log.LogError($"[Net] Loop error ({consecutiveErrors}): {e.Message}");

                    if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        Plugin.Log.LogError("[Net] Too many consecutive errors, disconnecting");
                        break;
                    }
                }

                Thread.Sleep(8);
            }
        }

        private NetworkMessage Preprocess(NetworkMessage msg)
        {
            Plugin.Log.LogDebug($"[Handle] {msg.Kind}");
            if (msg is HandshakeMessage hs)
                ConnectedPlayerName = hs.PlayerName;
            return msg;
        }

        private void Cleanup()
        {
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
            try { server?.Stop(); } catch { }
            stream = null;
            client = null;
            server = null;

            IsConnected = false;
            IsClient = false;
            IsHost = false;
            ConnectedPlayerName = null;
            pendingNpcUpdates.Clear();
        }

        private void UnityMain(Action a)
        {
            void Handler(NetworkMessage msg)
            {
                try { a(); }
                catch (Exception e) { Plugin.Log.LogError(e.ToString()); }
                finally { OnMessageReceived -= Handler; }
            }

            OnMessageReceived += Handler;
            incoming.Enqueue(new ChatMessage { PlayerName = "", Content = "" });
        }
    }
}