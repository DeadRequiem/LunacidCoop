using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;

namespace LunacidCoopMod
{
    public class SteamNetworkManager : MonoBehaviour
    {
        public static SteamNetworkManager Instance;

        public event Action<NetworkMessage> OnMessageReceived;
        public event Action OnConnected;
        public event Action OnDisconnected;

        // Fired when a peer (other than self) joins/leaves the lobby. Host fires from slot ops;
        // client fires by diffing LobbyStateMessage snapshots.
        public event Action<ClientSlot> OnPeerJoined;
        public event Action<ClientSlot> OnPeerLeft;

        public bool IsHost { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsConnected { get; private set; }
        public string ConnectedPlayerName { get; private set; }

        // Identity in handshakes. Steam mode: SteamID64 string. Local-test: per-process GUID.
        public string LocalPlayerId { get; private set; }

        // Client: slot assigned by host (-1 until handshake completes). Host: always 0.
        public int LocalSlot { get; private set; } = -1;

        // Host-side slot table indexed 0..3.
        private readonly SlotTable hostSlots = new SlotTable();
        public SlotTable HostSlots => hostSlots;

        // Client-side replica of the lobby roster, refreshed by LobbyStateMessage. Empty on host.
        private readonly Dictionary<int, ClientSlot> clientLobbyView = new Dictionary<int, ClientSlot>();

        // Client cache of other peers' pings (host->peer RTTs), keyed by PlayerId. Self ping is CurrentPing.
        private readonly Dictionary<string, float> peerPings = new Dictionary<string, float>();

        private const float PING_TABLE_INTERVAL = 2f;
        private float lastPingTableBroadcast = 0f;

        // "Primary" ping for HUD: first client on host, or host on client.
        public float CurrentPing { get; private set; }
        private float lastPingTime = 0f;
        private float pingStartTime = 0f;
        private const float PING_INTERVAL = 1f;
        // 10s gives ~10 ping cycles of slack; Steam P2P has occasional multi-second gaps under load.
        private const float PONG_TIMEOUT = 10f;

        private readonly PingTracker pingTracker = new PingTracker();

        // Auto-reconnect
        // Reads the config directly. It used to default false here while the menu toggle defaulted
        // true, so a player who never touched the setting saw it ticked while it was actually off.
        private bool shouldAutoReconnect => Plugin.AutoReconnect == null || Plugin.AutoReconnect.Value;
        private CSteamID lastHostId;
        private float reconnectAttemptTime = 0f;
        private int reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const float RECONNECT_DELAY = 3f;

        private CSteamID hostSteamId;
        private CSteamID currentLobbyId;       // hosted or joined Steam lobby
        public const int LOBBY_MAX_MEMBERS = 4;
        private bool steamInitialized = false;

        public CSteamID CurrentLobbyId => currentLobbyId;

        // Gate for Steam calls made outside this class; false in local-test mode and before Init succeeds.
        public bool SteamReady => steamInitialized;
        public CSteamID HostSteamId => hostSteamId;

        private readonly ConcurrentQueue<NetworkMessage> incoming = new ConcurrentQueue<NetworkMessage>();

        // Outgoing queue/batch/reliability. SendBytes routes to Steam P2P or LocalTransport.
        private readonly MessagePump pump = new MessagePump();

        private Callback<P2PSessionRequest_t> p2pSessionRequestCallback;
        private Callback<P2PSessionConnectFail_t> p2pSessionConnectFailCallback;

        // Both Callback and CallResult are wired; Lunacid only pumps the Callback bus reliably,
        // and the lobbyCreatedHandled guard prevents double processing if both fire.
        private Callback<LobbyCreated_t> lobbyCreatedCallback;
        private Callback<LobbyEnter_t> lobbyEnterCallback;
        private CallResult<LobbyCreated_t> lobbyCreatedCallResult;
        private CallResult<LobbyEnter_t> lobbyEnterCallResult;
        private Callback<GameLobbyJoinRequested_t> lobbyJoinRequestedCallback;
        private Callback<GameRichPresenceJoinRequested_t> richPresenceJoinRequestedCallback;
        private Callback<GameOverlayActivated_t> overlayActivatedCallback;
        private bool lobbyCreatedHandled;
        private bool lobbyEnteredHandled;

        // Steam overlay shown/hidden. Used to refresh status after the invite dialog closes.
        public event Action<bool> OnOverlayActivated;

        // Steam P2P receive path (per-sender buffers + Pump). LocalTransport handles TCP separately.
        private readonly PacketReceiver packetReceiver = new PacketReceiver();

        // Local-test mode: TCP-on-localhost transport instead of Steam P2P.
        public const int LOCAL_TEST_PORT = LocalTransport.DEFAULT_PORT;
        private bool IsLocalTest => Plugin.LocalTestMode?.Value == true;
        private readonly LocalTransport localTransport = new LocalTransport();

        // Synthetic CSteamID for the local TCP client. Must be non-Nil to avoid colliding with the host's slot 0 key.
        private static readonly CSteamID LOCAL_CLIENT_SYNTHETIC = new CSteamID(1UL);

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Local-test fallback identity; Steam mode overwrites with SteamID64 in InitializeSteam.
            LocalPlayerId = Guid.NewGuid().ToString();

            localTransport.OnPeerAccepted     += OnLocalPeerAccepted;
            localTransport.OnMessageParsed    += OnLocalMessageParsed;
            localTransport.OnPeerDisconnected += OnLocalPeerDisconnected;

            packetReceiver.OnMessageParsed    += OnP2PMessageParsed;

            // Route serialized packets through Steam P2P or LocalTransport based on mode.
            pump.SendBytes = (packet, length, target, reliable) =>
            {
                if (IsLocalTest)
                {
                    localTransport.Send(packet);
                }
                else
                {
                    if (!target.IsValid()) return;
                    var sendType = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
                    SteamNetworking.SendP2PPacket(target, packet, (uint)length, sendType);
                }
            };

            // Initialize early so invite/lobby callbacks are wired before MPMenu opens.
            // Without this, accepted invites can fire GameLobbyJoinRequested into nothing.
            if (Plugin.LocalTestMode == null || !Plugin.LocalTestMode.Value)
                InitializeSteam();

            Plugin.Log.LogInfo("[Steam] SteamNetworkManager created" + (steamInitialized ? " (Steam initialized)" : " (Steam pending)"));
        }

        private void InitializeSteam()
        {
            if (steamInitialized)
            {
                Plugin.Log.LogInfo("[Steam] Already initialized");
                return;
            }

            try
            {
                Plugin.Log.LogInfo("[Steam] Checking if Steam is running...");

                if (!SteamAPI.IsSteamRunning())
                {
                    Plugin.Log.LogError("[Steam] Steam is not running!");
                    return;
                }

                // Ensure SteamAPI is initialized. Lunacid presumably calls this itself, 
                // but SteamAPI.Init() is idempotent and harmless if already done
                bool initOk = SteamAPI.Init();
                Plugin.Log.LogInfo($"[Steam] SteamAPI.Init() returned {initOk}");
                if (!initOk)
                {
                    // Don't mark initialized; let a subsequent attempt retry once Steam is ready.
                    Plugin.Log.LogWarning("[Steam] Init failed, will retry on next host/join attempt");
                    return;
                }

                Plugin.Log.LogInfo("[Steam] Steam is running, setting up P2P...");

                // Allow P2P packet relay through Steam servers if direct connection fails
                SteamNetworking.AllowP2PPacketRelay(true);

                p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
                p2pSessionConnectFailCallback = Callback<P2PSessionConnectFail_t>.Create(OnP2PSessionConnectFail);

                // Both Callback and CallResult wired; the handled-guards prevent double processing.
                lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(r => OnLobbyCreated(r, false));
                lobbyEnterCallback = Callback<LobbyEnter_t>.Create(r => OnLobbyEnter(r, false));
                lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                lobbyEnterCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
                lobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
                richPresenceJoinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
                overlayActivatedCallback = Callback<GameOverlayActivated_t>.Create(OnOverlayActivatedCallback);

                Plugin.Log.LogInfo("[Steam] Getting Steam ID...");
                var mySteamId = SteamUser.GetSteamID();

                steamInitialized = true;

                // SteamID64 replaces the GUID fallback set in Awake.
                LocalPlayerId = mySteamId.m_SteamID.ToString();

                Plugin.Log.LogInfo($"[Steam] Ready! Your Steam ID: {mySteamId}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Steam] Failed to initialize: {e.Message}");
                Plugin.Log.LogError($"[Steam] Stack trace: {e.StackTrace}");
                steamInitialized = false;
            }
        }

        // Host fans out a received packet to every other client. No-op on client or in local-test.
        private void RelayPacket(byte[] packet, int length, CSteamID excludeSender, NetworkMessage typeRef)
        {
            if (IsLocalTest) return;
            if (!IsHost) return;

            var targets = new List<CSteamID>();
            foreach (var entry in hostSlots.Clients())
            {
                if (entry.SteamId == excludeSender) continue;
                if (!entry.SteamId.IsValid())       continue;
                targets.Add(entry.SteamId);
            }
            if (targets.Count == 0) return;

            pump.RelayPacket(packet, length, targets, typeRef);
            Plugin.Log.LogDebug($"[Net] Relayed {typeRef.Kind} to {targets.Count} client(s)");
        }

        // --- Lobby view ----------
        
        // From the authoritative roster, not a per-scene registry, so a split party still counts.
        public int LobbyPlayerCount => IsHost ? hostSlots.Count : clientLobbyView.Count;

        // Unified roster snapshot. Host uses the slot table; client uses the cached LobbyStateMessage.
        public List<ClientSlot> GetLobbyView()
        {
            if (IsHost) return hostSlots.Snapshot();

            var list = new List<ClientSlot>(clientLobbyView.Count);
            for (int s = 0; s < SlotTable.MAX_SLOTS; s++)
            {
                if (clientLobbyView.TryGetValue(s, out var entry)) list.Add(entry);
            }
            return list;
        }

        // Host: broadcast the slot table after every join/leave so client rosters stay in sync.
        private void BroadcastLobbyState()
        {
            if (!IsHost) return;

            var snap = hostSlots.Snapshot();
            int count = snap.Count;
            var msg = new LobbyStateMessage
            {
                SlotIndices = new int[count],
                SteamIds    = new string[count],
                PlayerNames = new string[count],
                PlayerIds   = new string[count],
                JoinTimes   = new float[count]
            };
            for (int i = 0; i < count; i++)
            {
                var e = snap[i];
                msg.SlotIndices[i] = e.Slot;
                msg.SteamIds[i]    = e.SteamId.m_SteamID.ToString();
                msg.PlayerNames[i] = e.PlayerName ?? "";
                msg.PlayerIds[i]   = e.PlayerId   ?? "";
                msg.JoinTimes[i]   = e.JoinTime;
            }
            Plugin.Log.LogInfo($"[Lobby] Broadcasting state ({count} slots) to clients");
            SendMessage(msg);
        }

        // Client: replace local view with host's snapshot and fire join/leave events for the diff.
        // Diffing on (slot, PlayerId) lets a slot whose occupant changes fire both leave + join.
        private void HandleLobbyStateOnClient(LobbyStateMessage msg)
        {
            if (msg == null)
            {
                Plugin.Log.LogWarning("[Lobby] Received null LobbyStateMessage");
                return;
            }
            if (msg.SlotIndices == null)
            {
                Plugin.Log.LogWarning("[Lobby] Received LobbyStateMessage with null SlotIndices");
                return;
            }
            int count = msg.SlotIndices.Length;
            Plugin.Log.LogInfo($"[Lobby] Received state with {count} slot(s)");

            // Rebuild the view from the parallel arrays; bounds-check each in case of truncation.
            var newView = new Dictionary<int, ClientSlot>();
            for (int i = 0; i < count; i++)
            {
                int slot = msg.SlotIndices[i];
                string steamIdStr = (msg.SteamIds    != null && i < msg.SteamIds.Length)    ? (msg.SteamIds[i]    ?? "") : "";
                string pname      = (msg.PlayerNames != null && i < msg.PlayerNames.Length) ? (msg.PlayerNames[i] ?? "") : "";
                string pid        = (msg.PlayerIds   != null && i < msg.PlayerIds.Length)   ? (msg.PlayerIds[i]   ?? "") : "";
                float joinTime    = (msg.JoinTimes   != null && i < msg.JoinTimes.Length)   ?  msg.JoinTimes[i]         : 0f;

                ulong rawSteamId = 0;
                if (!string.IsNullOrEmpty(steamIdStr)) ulong.TryParse(steamIdStr, out rawSteamId);

                newView[slot] = new ClientSlot
                {
                    Slot       = slot,
                    SteamId    = new CSteamID(rawSteamId),
                    PlayerName = pname,
                    PlayerId   = pid,
                    JoinTime   = joinTime
                };
            }

            // Old entries missing (or whose PlayerId changed) in the new view = left.
            foreach (var kvp in clientLobbyView)
            {
                bool stillThere = newView.TryGetValue(kvp.Key, out var newEntry)
                                  && newEntry.PlayerId == kvp.Value.PlayerId;
                if (!stillThere && kvp.Value.PlayerId != LocalPlayerId)
                {
                    Plugin.Log.LogInfo($"[Lobby] Peer left: slot {kvp.Value.Slot} {kvp.Value.PlayerName}");
                    OnPeerLeft?.Invoke(kvp.Value);
                }
            }

            // New entries missing from (or whose PlayerId changed in) the old view = joined.
            foreach (var kvp in newView)
            {
                bool wasThere = clientLobbyView.TryGetValue(kvp.Key, out var oldEntry)
                                && oldEntry.PlayerId == kvp.Value.PlayerId;
                if (!wasThere && kvp.Value.PlayerId != LocalPlayerId)
                {
                    Plugin.Log.LogInfo($"[Lobby] Peer joined: slot {kvp.Value.Slot} {kvp.Value.PlayerName}");
                    OnPeerJoined?.Invoke(kvp.Value);
                }
            }

            // Commit.
            clientLobbyView.Clear();
            foreach (var kvp in newView) clientLobbyView[kvp.Key] = kvp.Value;
        }

        // Host: broadcast each client's measured ping at PING_TABLE_INTERVAL so clients see peer pings.
        private void BroadcastPingTable()
        {
            if (!IsHost) return;
            int clientCount = hostSlots.ClientCount;
            if (clientCount == 0) return;

            var playerIds = new string[clientCount];
            var pings     = new float[clientCount];
            int i = 0;
            foreach (var entry in hostSlots.Clients())
            {
                playerIds[i] = entry.PlayerId ?? "";
                pings[i]     = pingTracker.GetClientPingMs(entry.SteamId);
                i++;
            }
            SendMessage(new PingTableMessage { PlayerIds = playerIds, PingsMs = pings });
        }

        // Client: update our cached peer-ping table from the host's broadcast.
        private void HandlePingTableOnClient(PingTableMessage msg)
        {
            if (msg?.PlayerIds == null || msg.PingsMs == null) return;
            int n = Mathf.Min(msg.PlayerIds.Length, msg.PingsMs.Length);
            for (int i = 0; i < n; i++)
            {
                if (string.IsNullOrEmpty(msg.PlayerIds[i])) continue;
                peerPings[msg.PlayerIds[i]] = msg.PingsMs[i];
            }
        }

        // Host's PlayerId from either side. Returns false pre-roster.
        public bool TryGetHostPlayerId(out string playerId)
        {
            if (IsHost)
            {
                playerId = LocalPlayerId;
                return !string.IsNullOrEmpty(playerId);
            }
            if (clientLobbyView.TryGetValue(SlotTable.HOST_SLOT, out var entry))
            {
                playerId = entry.PlayerId;
                return !string.IsNullOrEmpty(playerId);
            }
            playerId = null;
            return false;
        }

        // Per-peer ping lookup. Host uses PingTracker; client uses CurrentPing for host, cached table for others.
        public float GetPingForPeer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return 0f;

            if (IsHost)
            {
                if (hostSlots.TryGetByPlayerId(playerId, out var slot))
                    return pingTracker.GetClientPingMs(slot.SteamId);
                return 0f;
            }
            // Host's PlayerId on the client routes to our own measured ping.
            if (clientLobbyView.TryGetValue(SlotTable.HOST_SLOT, out var slot0) && slot0.PlayerId == playerId)
                return CurrentPing;
            return peerPings.TryGetValue(playerId, out float p) ? p : 0f;
        }

        // --- Host slot helpers ----------

        // Drop per-client state for one disconnected client; host stays up.
        private void TearDownClient(CSteamID clientId, string reason)
        {
            if (!IsHost) return;

            // Snapshot the entry before freeing so OnPeerLeft fires with full info.
            ClientSlot leavingEntry = default;
            bool hadSlot = hostSlots.TryGetSlot(clientId, out int slot)
                           && hostSlots.TryGetByIndex(slot, out leavingEntry);

            if (hadSlot)
            {
                hostSlots.FreeSlotByIndex(slot);
                Plugin.Log.LogInfo($"[Net] Freed slot {slot} (client {clientId} left: {reason})");
            }
            packetReceiver.RemoveBuffer(clientId);
            pingTracker.RemoveClient(clientId);

            // Close P2P session (idempotent).
            if (!IsLocalTest && clientId.IsValid())
            {
                try { SteamNetworking.CloseP2PSessionWithUser(clientId); } catch { }
            }

            RefreshAggregateConnectionState();

            // Fire locally on host; clients pick it up from the next LobbyStateMessage diff.
            if (hadSlot)
                OnPeerLeft?.Invoke(leavingEntry);

            if (hostSlots.ClientCount > 0)
                BroadcastLobbyState();

            if (hostSlots.ClientCount == 0)
            {
                // Lobby stays open with no clients - "waiting" state.
                Plugin.Log.LogInfo("[Net] All clients disconnected; lobby remains open");
                OnDisconnected?.Invoke();
            }
        }

        // Recompute IsConnected/ConnectedPlayerName/CurrentPing from the slot table. Host only.
        private void RefreshAggregateConnectionState()
        {
            if (!IsHost) return;

            bool any = hostSlots.ClientCount > 0;
            IsConnected = any;
            if (!any)
            {
                ConnectedPlayerName = null;
                CurrentPing = 0f;
                return;
            }

            // Use the lowest-slot client as the "primary" for legacy single-peer UI.
            foreach (var entry in hostSlots.Clients())
            {
                ConnectedPlayerName = entry.PlayerName;
                CurrentPing = pingTracker.GetClientPingMs(entry.SteamId);
                return;
            }
        }

        // --- P2P session callbacks ----------

        private void OnP2PSessionRequest(P2PSessionRequest_t request)
        {
            Plugin.Log.LogInfo($"[Steam] P2P session request from {request.m_steamIDRemote}");

            if (IsHost)
            {
                // Accept the session and pre-create the buffer. Slot allocation waits for
                // the client's handshake (which carries their PlayerId).
                SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
                packetReceiver.GetOrCreateBuffer(request.m_steamIDRemote);
                Plugin.Log.LogInfo($"[Steam] Accepted P2P session from {request.m_steamIDRemote} (awaiting handshake)");

                // Send our handshake immediately; AssignedSlot is -1 until we learn their PlayerId.
                SendMessage(new HandshakeMessage
                {
                    PlayerName    = SteamFriends.GetPersonaName(),
                    ModVersion    = Plugin.ModVersion,
                    ProtocolVersion = Plugin.ProtocolVersion,
                    PlayerId      = LocalPlayerId,
                    AssignedSlot  = -1
                }, request.m_steamIDRemote);
            }
            // Client must also Accept; both sides need to call it before unreliable packets flow.
            else if (IsClient && request.m_steamIDRemote == hostSteamId)
            {
                SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
                Plugin.Log.LogInfo($"[Steam] Accepted session from host {request.m_steamIDRemote}");
            }
            else
            {
                Plugin.Log.LogWarning($"[Steam] Ignoring P2P request from {request.m_steamIDRemote} (not host)");
            }
        }

        private void OnP2PSessionConnectFail(P2PSessionConnectFail_t failure)
        {
            Plugin.Log.LogError($"[Steam] P2P connection failed with {failure.m_steamIDRemote}: {failure.m_eP2PSessionError}");

            if (IsClient)
            {
                Plugin.Log.LogError("[Steam] Failed to connect to host");

                if (shouldAutoReconnect)
                {
                    Plugin.Log.LogInfo("[Steam] Will attempt auto-reconnect...");
                    DropPeerConnection();
                    reconnectAttemptTime = Time.time;
                }
                else
                {
                    DropPeerConnection();
                    OnDisconnected?.Invoke();
                }
            }
        }

        private bool _firstPumpLogged;

        private void Update()
        {
            if (!IsLocalTest && !steamInitialized) return;

            // Pump Steam callbacks; Lunacid's own pump skips some lobby events under BepInEx.
            // Double-pumping is harmless.
            if (!IsLocalTest)
            {
                try
                {
                    SteamAPI.RunCallbacks();
                    if (!_firstPumpLogged)
                    {
                        _firstPumpLogged = true;
                        Plugin.Log.LogInfo("[Steam] First RunCallbacks pump succeeded");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Steam] RunCallbacks threw: {e.Message}");
                }
            }

            while (incoming.TryDequeue(out var msg))
            {
                try { OnMessageReceived?.Invoke(msg); }
                catch (Exception e) { Plugin.Log.LogError($"[Net] Dispatch of {msg?.Kind} threw: {e}"); }
            }

            if (IsLocalTest)
                localTransport.Pump();
            else
                packetReceiver.Pump();

            // NPC batching is throttled inside MessagePump.FlushNpcBatchTo; safe to call every frame.
            SendBatchedNpcUpdates();

            // Host: periodic peer-ping broadcast so clients can show pings they don't measure themselves.
            if (IsHost && IsConnected && Time.time - lastPingTableBroadcast >= PING_TABLE_INTERVAL)
            {
                BroadcastPingTable();
                lastPingTableBroadcast = Time.time;
            }

            SendOutgoingMessages();

            // Host pings every client (separate RTT clocks); client pings only the host.
            if (IsConnected && Time.time - lastPingTime >= PING_INTERVAL)
            {
                if (IsHost)
                {
                    pingStartTime = Time.time;
                    foreach (var entry in hostSlots.Clients())
                        SendMessage(new PingMessage { RequestTime = pingStartTime }, entry.SteamId);
                }
                else
                {
                    SendPingRequest();
                }
                lastPingTime = Time.time;
            }

            // Silent-disconnect watchdog: Steam P2P has no session-ended event, so infer it from pong silence.
            // Host frees only the dead client's slot; client fully drops on host timeout.
            if (IsHost && IsConnected)
            {
                var clientIds = new List<CSteamID>();
                foreach (var entry in hostSlots.Clients()) clientIds.Add(entry.SteamId);

                var dead = pingTracker.FindTimedOutClients(clientIds, Time.time, PONG_TIMEOUT);
                if (dead != null)
                {
                    foreach (var id in dead)
                    {
                        Plugin.Log.LogWarning($"[Net] Client {id} silent for {PONG_TIMEOUT}s - dropping slot");
                        TearDownClient(id, "pong timeout");
                    }
                }
            }
            else if (IsConnected && pingTracker.IsHostTimedOut(Time.time, PONG_TIMEOUT))
            {
                Plugin.Log.LogWarning($"[Net] No pong received in {PONG_TIMEOUT}s - assuming host disconnected");
                DropPeerConnection();
                OnDisconnected?.Invoke();
            }

            // Auto-reconnect only applies to Steam P2P, and only to a client that actually lost a host. 
            if (!IsLocalTest && shouldAutoReconnect && !IsConnected && !IsHost && !IsClient
                && lastHostId.IsValid() && reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
            {
                if (Time.time - reconnectAttemptTime >= RECONNECT_DELAY)
                {
                    reconnectAttempts++;
                    Plugin.Log.LogInfo($"[Steam] Auto-reconnect attempt {reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS}");
                    ConnectToHost(lastHostId);
                    reconnectAttemptTime = Time.time;
                }
            }
        }

        // LocalTransport event handlers. Layer role-aware behavior on top of raw TCP framing.
        // Host accepted a TCP client. Send our initial handshake; slot is finalized when client's handshake arrives.
        private void OnLocalPeerAccepted()
        {
            var hs = NetworkMessageHandler.Serialize(new HandshakeMessage
            {
                PlayerName   = "LocalHost",
                ModVersion   = Plugin.ModVersion,
                ProtocolVersion = Plugin.ProtocolVersion,
                PlayerId     = LocalPlayerId,
                AssignedSlot = -1
            });
            localTransport.Send(hs);
        }

        // Tag the parsed message with the synthetic sender ID for our role and enqueue.
        private void OnLocalMessageParsed(NetworkMessage msg)
        {
            CSteamID sender = IsHost ? LOCAL_CLIENT_SYNTHETIC : default;
            incoming.Enqueue(Preprocess(msg, sender));
        }

        // TCP error already tore down transport state; mirror it in slot/role state.
        private void OnLocalPeerDisconnected()
        {
            if (IsHost)
            {
                if (hostSlots.TryGetSlot(LOCAL_CLIENT_SYNTHETIC, out int _))
                    TearDownClient(LOCAL_CLIENT_SYNTHETIC, "transport error");
            }
            else
            {
                DropPeerConnection();
                OnDisconnected?.Invoke();
            }
        }

        // One parsed P2P message: preprocess + enqueue, then relay to other clients if applicable.
        private void OnP2PMessageParsed(NetworkMessage message, CSteamID senderId, byte[] packetData, int bytesRead)
        {
            if (IsHost && !(message is HandshakeMessage) && !hostSlots.TryGetSlot(senderId, out _))
            {
                Plugin.Log.LogDebug($"[Net] Dropped {message.Kind} from slotless peer {senderId}");
                return;
            }

            incoming.Enqueue(Preprocess(message, senderId));

            if (IsHost && MessagePump.IsRelayable(message))
                RelayPacket(packetData, bytesRead, senderId, message);
        }

        // Drain the outgoing queue via the SendBytes callback wired in Awake.
        private void SendOutgoingMessages() => pump.Pump();

        public void StartHost()
        {
            if (IsConnected || IsHost)
            {
                Plugin.Log.LogWarning("[Net] Already hosting or connected");
                return;
            }
            if (IsClient)
            {
                Plugin.Log.LogWarning("[Net] Cannot host - a join is already in flight");
                return;
            }

            if (IsLocalTest)
            {
                Application.runInBackground = true;
                IsHost = true;
                hostSlots.SetHostSlot(CSteamID.Nil, "LocalHost", LocalPlayerId);
                if (!localTransport.StartHosting(LOCAL_TEST_PORT))
                    IsHost = false;
                return;
            }

            try
            {
                Plugin.Log.LogInfo("[Steam] StartHost called");

                if (!steamInitialized)
                {
                    InitializeSteam();
                    if (!steamInitialized)
                    {
                        Plugin.Log.LogError("[Steam] Cannot host - Steam initialization failed!");
                        return;
                    }
                }

                Application.runInBackground = true;
                IsHost = true;
                hostSteamId = SteamUser.GetSteamID();
                hostSlots.SetHostSlot(hostSteamId, SteamFriends.GetPersonaName(), LocalPlayerId);
                Plugin.Log.LogInfo($"[Steam] Hosting! Your Steam ID: {hostSteamId}");

                // Friends-only lobby; OnLobbyCreated wires rich presence so "Join Game" appears under us.
                lobbyCreatedHandled = false;
                SteamAPICall_t createHandle = SteamMatchmaking.CreateLobby(
                    ELobbyType.k_ELobbyTypeFriendsOnly, LOBBY_MAX_MEMBERS);
                Plugin.Log.LogInfo($"[Steam] Lobby creation requested... handle={createHandle.m_SteamAPICall}");
                if (createHandle == SteamAPICall_t.Invalid)
                {
                    Plugin.Log.LogError("[Steam] CreateLobby returned an invalid handle - Steam may not be fully initialized");
                }
                else
                {
                    lobbyCreatedCallResult.Set(createHandle, OnLobbyCreated);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Steam] Host start failed: {e.Message}\n{e.StackTrace}");
                IsHost = false;
            }
        }

        // --- Lobby callbacks ----------

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (lobbyCreatedHandled) return;
            lobbyCreatedHandled = true;

            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                // IsHost was set optimistically in StartHost. Leaving it set strands the player on the
                // hosting screen with no lobby, a dead INVITE FRIEND button and no rich presence.
                Plugin.Log.LogError($"[Steam] Lobby creation failed: result={result.m_eResult} ioFailure={ioFailure}");
                IsHost = false;
                hostSlots.Clear();
                MPMenu.Instance?.SetStatus("COULD NOT CREATE LOBBY - NOT HOSTING.");
                return;
            }

            currentLobbyId = new CSteamID(result.m_ulSteamIDLobby);
            Plugin.Log.LogInfo($"[Steam] Lobby created: {currentLobbyId}");

            // Canonical Steam rich-presence "connect" key drives the friends-list Join Game button.
            SteamFriends.SetRichPresence("connect", $"+connect_lobby {currentLobbyId.m_SteamID}");
            CoopPresence.Refresh();
        }

        private void OnLobbyEnter(LobbyEnter_t evt, bool ioFailure)
        {
            if (lobbyEnteredHandled) return;
            lobbyEnteredHandled = true;

            if (ioFailure)
            {
                Plugin.Log.LogError("[Steam] Lobby enter failed (ioFailure)");
                IsClient = false;
                MPMenu.Instance?.SetStatus("COULD NOT REACH THAT LOBBY.");
                return;
            }

            var response = (EChatRoomEnterResponse)evt.m_EChatRoomEnterResponse;
            if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogError($"[Steam] Lobby enter refused: {response}");
                IsClient = false;
                MPMenu.Instance?.SetStatus($"COULD NOT JOIN: {response.ToString().ToUpper()}");
                return;
            }
            currentLobbyId = new CSteamID(evt.m_ulSteamIDLobby);
            Plugin.Log.LogInfo($"[Steam] Entered lobby: {currentLobbyId}");

            // Host doesn't connect to itself.
            if (IsHost) return;

            // Client just entered the host's lobby; connect P2P to the lobby owner.
            var owner = SteamMatchmaking.GetLobbyOwner(currentLobbyId);
            if (!owner.IsValid())
            {
                Plugin.Log.LogError("[Steam] Lobby owner invalid - cannot connect");
                IsClient = false;
                return;
            }

            Plugin.Log.LogInfo($"[Steam] Lobby owner is {owner} - initiating P2P connection");
            ConnectToHost(owner);
        }

        // Fires when a friend clicks "Join Game" in their Steam friends list (in-game).
        // Also fires when accepting an invite while the game is running.
        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t evt)
        {
            Plugin.Log.LogInfo($"[Steam] Lobby join requested: {evt.m_steamIDLobby} (from friend {evt.m_steamIDFriend})");
            JoinLobby(evt.m_steamIDLobby);
        }

        // Fires whenever the Steam overlay opens or closes. 
        // Using it to update the UI status when the user closes the invite dialog
        // Steam doesn't tell us whether they actually sent an invite (no callback for that which I'm aware of)
        private void OnOverlayActivatedCallback(GameOverlayActivated_t evt)
        {
            bool active = evt.m_bActive != 0;
            Plugin.Log.LogInfo($"[Steam] Overlay {(active ? "opened" : "closed")}");
            OnOverlayActivated?.Invoke(active);
        }

        // "+connect_lobby <id>" launch path. Best-effort; under BepInEx, Steam may not be ready yet.
        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t evt)
        {
            Plugin.Log.LogInfo($"[Steam] Rich-presence join requested: connect='{evt.m_rgchConnect}'");
            // Parse "+connect_lobby <id>" if present
            string s = evt.m_rgchConnect ?? "";
            const string token = "+connect_lobby ";
            int idx = s.IndexOf(token);
            if (idx >= 0 && ulong.TryParse(s.Substring(idx + token.Length).Trim(), out ulong lobbyId))
            {
                JoinLobby(new CSteamID(lobbyId));
            }
        }

        // --- Public API for invite UI / join paths ----------

        // Open the Steam overlay's invite dialog filtered to our lobby. No-op if no lobby yet.
        public void ActivateInviteOverlay()
        {
            if (!steamInitialized)
            {
                Plugin.Log.LogWarning("[Steam] Cannot open invite overlay - Steam not initialized");
                return;
            }
            if (!currentLobbyId.IsValid())
            {
                Plugin.Log.LogWarning("[Steam] Cannot open invite overlay - no active lobby");
                return;
            }
            SteamFriends.ActivateGameOverlayInviteDialog(currentLobbyId);
            Plugin.Log.LogInfo($"[Steam] Opened invite overlay for lobby {currentLobbyId}");
        }

        // Join a Steam lobby by ID. OnLobbyEnter finishes the P2P handshake on success.
        public void JoinLobby(CSteamID lobbyId)
        {
            if (!steamInitialized) InitializeSteam();
            if (!steamInitialized) { Plugin.Log.LogError("[Steam] Cannot join lobby - Steam not initialized"); return; }
            if (IsHost || IsClient)
            {
                Plugin.Log.LogWarning("[Steam] Cannot join lobby - already in a session");
                return;
            }

            Application.runInBackground = true;
            IsClient = true;
            // Armed here, so reset here. Cleanup() is the only other place that clears it, and an
            // involuntary drop goes through DropPeerConnection instead, leaving it latched.
            lobbyEnteredHandled = false;
            SteamAPICall_t joinHandle = SteamMatchmaking.JoinLobby(lobbyId);
            lobbyEnterCallResult.Set(joinHandle, OnLobbyEnter);
            Plugin.Log.LogInfo($"[Steam] Joining lobby {lobbyId}...");
        }

        public void ConnectToLocalHost()
        {
            if (IsConnected || IsClient) return;

            Application.runInBackground = true;
            IsClient = true;

            if (!localTransport.ConnectAsClient(LOCAL_TEST_PORT))
            {
                Cleanup();
                return;
            }

            // Stream is ready; send handshake immediately.
            var handshake = NetworkMessageHandler.Serialize(new HandshakeMessage
            {
                PlayerName   = "LocalClient",
                ModVersion   = Plugin.ModVersion,
                ProtocolVersion = Plugin.ProtocolVersion,
                PlayerId     = LocalPlayerId,
                AssignedSlot = -1
            });
            localTransport.Send(handshake);
        }

        public void ConnectToHost(CSteamID hostId)
        {
            // Don't gate on IsClient - JoinLobby sets it before this runs, so that would abort invite joins.
            if (IsConnected) return;
            if (IsHost) return;

            if (!steamInitialized)
            {
                InitializeSteam();
                if (!steamInitialized)
                {
                    Plugin.Log.LogError("[Steam] Cannot connect - Steam initialization failed!");
                    return;
                }
            }

            try
            {
                Application.runInBackground = true;
                hostSteamId = hostId;
                lastHostId = hostId;
                IsClient = true;

                // Proactively accept; the reciprocal callback also accepts, but doing it now avoids waiting on callback timing.
                SteamNetworking.AcceptP2PSessionWithUser(hostId);

                Plugin.Log.LogInfo($"[Steam] Connecting to {hostId}...");
                StartCoroutine(RetryHandshakeUntilConnected(hostId));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Steam] Connect failed: {e.Message}");
                Cleanup();
            }
        }

        // Resend handshake until IsConnected. Steam P2P sometimes swallows the first packet to open a session.
        private IEnumerator RetryHandshakeUntilConnected(CSteamID hostId)
        {
            const int MAX_ATTEMPTS = 10;
            const float RETRY_INTERVAL = 1f;

            for (int i = 0; i < MAX_ATTEMPTS; i++)
            {
                if (IsConnected || !IsClient) yield break;

                Plugin.Log.LogInfo($"[Steam] Sending handshake (attempt {i + 1}/{MAX_ATTEMPTS})");
                SendMessage(new HandshakeMessage
                {
                    PlayerName   = SteamFriends.GetPersonaName(),
                    ModVersion   = Plugin.ModVersion,
                ProtocolVersion = Plugin.ProtocolVersion,
                    PlayerId     = LocalPlayerId,
                    AssignedSlot = -1
                }, hostId);

                yield return new WaitForSeconds(RETRY_INTERVAL);
            }

            if (!IsConnected)
            {
                // Without clearing IsClient, JoinLobby refuses every later invite.
                Plugin.Log.LogError($"[Steam] Handshake exhausted after {MAX_ATTEMPTS} attempts - connection failed");
                DropPeerConnection();
                OnDisconnected?.Invoke();
            }
        }

        public void EnableAutoReconnect(bool enable)
        {
            if (Plugin.AutoReconnect != null) Plugin.AutoReconnect.Value = enable;
            if (!enable)
            {
                reconnectAttempts = 0;
            }
            Plugin.Log.LogInfo($"[Steam] Auto-reconnect {(enable ? "enabled" : "disabled")}");
        }

        private void SendPingRequest()
        {
            pingStartTime = Time.time;
            SendMessage(new PingMessage { RequestTime = pingStartTime });
        }

        public void SendMessage(NetworkMessage message)
        {
            if (!IsConnected || message == null) return;

            if (IsHost)
            {
                // Host broadcast: one enqueue per client. Local-test has a single peer.
                if (IsLocalTest)
                {
                    SendMessage(message, default);
                    return;
                }
                foreach (var entry in hostSlots.Clients())
                    SendMessage(message, entry.SteamId);
            }
            else
            {
                CSteamID target = IsLocalTest ? default : hostSteamId;
                SendMessage(message, target);
            }
        }

        private void SendMessage(NetworkMessage message, CSteamID target)
        {
            if (message == null) return;
            if (!IsLocalTest && !target.IsValid()) return;
            pump.Enqueue(message, target);
        }

        // Host-only directed send (e.g. per-client catch-up flows). No-op on client; unknown targets are dropped.
        public void SendTo(NetworkMessage message, CSteamID target)
        {
            if (message == null) return;
            if (!IsConnected) return;
            if (!IsHost)
            {
                Plugin.Log.LogWarning("[Net] SendTo called on a non-host; ignoring");
                return;
            }
            if (!hostSlots.TryGetSlot(target, out _))
            {
                Plugin.Log.LogWarning($"[Net] SendTo: target {target} is not a known client; dropping");
                return;
            }
            SendMessage(message, target);
        }

        // Build the broadcast target list for our role and let the pump flush its NPC batch into it.
        private void SendBatchedNpcUpdates()
        {
            var targets = new List<CSteamID>();
            if (IsLocalTest)
            {
                targets.Add(default);
            }
            else if (IsHost)
            {
                foreach (var entry in hostSlots.Clients())
                    targets.Add(entry.SteamId);
                if (targets.Count == 0) { pump.ClearPendingNpcUpdates(); return; }
            }
            else
            {
                if (!hostSteamId.IsValid()) return;
                targets.Add(hostSteamId);
            }

            pump.FlushNpcBatchTo(targets);
        }

        public void Disconnect()
        {
            // Tell the other side before the socket goes away, or they wait out the full 10s watchdog
            // with damage suppressed and NPCs puppeted by nobody.
            if (IsConnected)
            {
                SendMessage(new DisconnectNoticeMessage
                {
                    Reason = IsHost ? "Host ended the session" : "Player left"
                });
                pump.Pump();   // flush it now; Cleanup clears the queue
            }

            if (IsHost)
            {
                foreach (var entry in hostSlots.Clients())
                {
                    if (entry.SteamId.IsValid())
                    {
                        try { SteamNetworking.CloseP2PSessionWithUser(entry.SteamId); } catch { }
                    }
                }
            }
            else if (IsClient && hostSteamId.IsValid())
            {
                SteamNetworking.CloseP2PSessionWithUser(hostSteamId);
            }

            Cleanup();
            OnDisconnected?.Invoke();
            Plugin.Log.LogInfo("[Steam] Disconnected");
        }

        private NetworkMessage Preprocess(NetworkMessage msg, CSteamID sender)
        {
            Plugin.Log.LogDebug($"[Handle] {msg.Kind} from {sender}");

            if (msg is HandshakeMessage hs)
            {
                // The host answers every incoming session with a provisional slot -1, before it decides anything.
                if (hs.AssignedSlot >= 0) reconnectAttempts = 0;

                if (IsHost)
                {
                    HandleHandshakeOnHost(hs, sender);
                }
                else if (IsClient)
                {
                    HandleHandshakeOnClient(hs);
                }
            }
            // Host-originated state; clients apply it, host ignores.
            else if (msg is DisconnectNoticeMessage notice)
            {
                HandleDisconnectNotice(notice, sender);
            }
            else if (msg is LobbyStateMessage lobby)
            {
                if (IsClient) HandleLobbyStateOnClient(lobby);
            }
            else if (msg is PingTableMessage pingTable)
            {
                if (IsClient) HandlePingTableOnClient(pingTable);
            }
            else if (msg is PingMessage ping)
            {
                // Incoming ping is proof of life; respond with a pong.
                if (IsHost) pingTracker.MarkClientAlive(sender, Time.time);
                else        pingTracker.MarkHostAlive(Time.time);

                SendMessage(new PongMessage { RequestTime = ping.RequestTime }, sender);
            }
            else if (msg is PongMessage pong)
            {
                float ping_ms = (Time.time - pong.RequestTime) * 1000f;
                if (IsHost)
                {
                    pingTracker.RecordPongFromClient(sender, Time.time, ping_ms);
                    // Recompute the "primary" slot each pong since clients leave dynamically.
                    if (hostSlots.TryGetSlot(sender, out int slot))
                    {
                        int primarySlot = -1;
                        foreach (var e in hostSlots.Clients()) { primarySlot = e.Slot; break; }
                        if (slot == primarySlot) CurrentPing = ping_ms;
                    }
                }
                else
                {
                    pingTracker.RecordPongFromHost(Time.time, ping_ms);
                    CurrentPing = ping_ms;
                }
                Plugin.Log.LogDebug($"[Steam] Ping from {sender}: {ping_ms:F0}ms");
            }

            return msg;
        }

        // Host: allocate the lowest free slot to the sender and reply with the assignment.
        private void HandleHandshakeOnHost(HandshakeMessage hs, CSteamID sender)
        {
            if (!sender.IsValid() && !IsLocalTest)
            {
                Plugin.Log.LogWarning("[Steam] Handshake from invalid sender; ignoring");
                return;
            }

            // Defensive accept; reconnect paths can skip OnP2PSessionRequest.
            if (!IsLocalTest && sender.IsValid())
            {
                try { SteamNetworking.AcceptP2PSessionWithUser(sender); } catch { }
            }

            string playerName = !string.IsNullOrEmpty(hs.PlayerName) ? hs.PlayerName : "Unknown";
            string playerId   = !string.IsNullOrEmpty(hs.PlayerId)   ? hs.PlayerId   : sender.ToString();

            // Duplicate handshake (client retry before our reply landed). Just resend their assignment.
            if (hostSlots.TryGetByPlayerId(playerId, out var existing))
            {
                Plugin.Log.LogDebug($"[Steam] Duplicate handshake from {playerName} (slot {existing.Slot}); resending assignment");
                SendMessage(new HandshakeMessage
                {
                    PlayerName   = SteamFriends.GetPersonaName(),
                    ModVersion   = Plugin.ModVersion,
                ProtocolVersion = Plugin.ProtocolVersion,
                    PlayerId     = LocalPlayerId,
                    AssignedSlot = existing.Slot
                }, sender);
                // This path skips OnPeerJoined, so everything that rides it has to be redone here.
                CoopConfig.SendConfigTo(sender);
                pingTracker.SeedClient(sender, Time.time);
                if (hostSlots.TryGetByIndex(existing.Slot, out var rejoined))
                    OnPeerJoined?.Invoke(rejoined);
                return;
            }

            // Refuse a build we cannot talk to, rather than letting it desync silently. ProtocolVersion
            // is bumped by hand when message types change; 0 means a peer from before this check.
            if (hs.ProtocolVersion != Plugin.ProtocolVersion)
            {
                Plugin.Log.LogWarning($"[Steam] Rejecting {playerName}: protocol {hs.ProtocolVersion} != {Plugin.ProtocolVersion}");
                SendMessage(new DisconnectNoticeMessage
                {
                    Reason = $"Mod version mismatch (protocol {hs.ProtocolVersion} vs {Plugin.ProtocolVersion})"
                }, sender);
                StartCoroutine(CloseSessionAfterNotice(sender));
                return;
            }

            int slot = hostSlots.AllocateSlot(sender, playerName, playerId);
            if (slot < 0)
            {
                Plugin.Log.LogWarning($"[Steam] Lobby full; rejecting handshake from {playerName} ({sender})");
                SendMessage(new DisconnectNoticeMessage { Reason = "Session is full" }, sender);
                StartCoroutine(CloseSessionAfterNotice(sender));
                return;
            }

            pingTracker.SeedClient(sender, Time.time);

            bool wasFirstClient = hostSlots.ClientCount == 1;
            Plugin.Log.LogInfo($"[Steam] Slot {slot} <- {playerName} (PlayerId={playerId})");

            // Reply with the assigned slot so the client can populate LocalSlot.
            SendMessage(new HandshakeMessage
            {
                PlayerName   = SteamFriends.GetPersonaName(),
                ModVersion   = Plugin.ModVersion,
                ProtocolVersion = Plugin.ProtocolVersion,
                PlayerId     = LocalPlayerId,
                AssignedSlot = slot
            }, sender);

            RefreshAggregateConnectionState();

            // Legacy "any peer connected" event - fires only once per hosting session.
            if (wasFirstClient)
                OnConnected?.Invoke();

            // Broadcast updated roster; clients diff to fire their own OnPeerJoined.
            BroadcastLobbyState();

            // Host doesn't process its own broadcast, so fire OnPeerJoined locally too.
            if (hostSlots.TryGetByIndex(slot, out var newEntry))
                OnPeerJoined?.Invoke(newEntry);
        }

        // Give the reliable notice a frame to leave the queue before we tear the session down.
        private IEnumerator CloseSessionAfterNotice(CSteamID target)
        {
            yield return null;
            yield return null;
            if (!IsLocalTest && target.IsValid())
            {
                try { SteamNetworking.CloseP2PSessionWithUser(target); } catch { }
            }
        }

        private void HandleDisconnectNotice(DisconnectNoticeMessage m, CSteamID sender)
        {
            string reason = string.IsNullOrEmpty(m.Reason) ? "Session ended" : m.Reason;

            if (IsHost)
            {
                // A client saying goodbye: free its slot now instead of waiting out the watchdog.
                Plugin.Log.LogInfo($"[Net] Client {sender} left: {reason}");
                TearDownClient(sender, reason);
                return;
            }

            Plugin.Log.LogWarning($"[Net] Disconnected by host: {reason}");
            MPMenu.Instance?.SetStatus(reason.ToUpper());

            // DropPeerConnection keeps lastHostId for a real drop. A refusal is not one.
            lastHostId = CSteamID.Nil;
            DropPeerConnection();
            OnDisconnected?.Invoke();
        }

        // Client: record the assigned slot from the host's reply and go live.
        private void HandleHandshakeOnClient(HandshakeMessage hs)
        {
            ConnectedPlayerName = hs.PlayerName;
            pingTracker.MarkHostAlive(Time.time);

            // The provisional handshake carries slot -1 before the host has allocated anything.
            if (hs.AssignedSlot < 0)
            {
                Plugin.Log.LogInfo($"[Steam] Provisional handshake from {hs.PlayerName}; awaiting slot");
                return;
            }

            bool wasUnassigned = LocalSlot < 0;
            LocalSlot = hs.AssignedSlot;
            if (wasUnassigned)
                Plugin.Log.LogInfo($"[Steam] Host assigned us slot {LocalSlot}");

            Plugin.Log.LogInfo($"[Steam] Handshake received from {hs.PlayerName} (slot={hs.AssignedSlot})");

            if (!IsConnected)
            {
                IsConnected = true;
                OnConnected?.Invoke();
            }
        }

        // Full teardown including our role; use only when we're leaving the session ourselves.
        private void Cleanup()
        {
            DropPeerConnection();

            // Drop our role and undo the host-slot re-seat DropPeerConnection did.
            IsHost = false;
            IsClient = false;
            hostSteamId = CSteamID.Nil;
            hostSlots.Clear();
            LocalSlot = -1;

            localTransport.Stop();

            // Leave the lobby and clear rich presence so we no longer appear joinable.
            if (steamInitialized)
            {
                if (currentLobbyId.IsValid())
                {
                    try { SteamMatchmaking.LeaveLobby(currentLobbyId); } catch { }
                    currentLobbyId = CSteamID.Nil;
                }
                try { SteamFriends.ClearRichPresence(); } catch { }
                CoopPresence.Invalidate();
            }

            // Reset lobby-callback guards so the next host/join attempt fires its handler.
            lobbyCreatedHandled = false;
            lobbyEnteredHandled = false;

            // Leaving deliberately: forget the target so auto-reconnect can't drag us back into the
            // session we just left. The user's toggle preference is left alone.
            lastHostId = CSteamID.Nil;
            reconnectAttempts = 0;
        }

        // Tear down ALL peer connections but keep our role (host stays hosting, client drops out).
        // For per-client drops while other clients survive, use TearDownClient instead.
        private void DropPeerConnection()
        {
            IsConnected = false;
            ConnectedPlayerName = null;
            LocalSlot = -1;

            // Wipe everything; host re-seats slot 0 below.
            hostSlots.Clear();
            packetReceiver.Clear();
            pingTracker.Clear();
            clientLobbyView.Clear();
            peerPings.Clear();
            pump.Clear();

            // Keep the listener; only the peer connection is dropped.
            localTransport.StopPeer();

            // Re-seat host's own slot 0 so the table still says "I am the host" while waiting.
            if (IsHost)
            {
                CSteamID myId = !IsLocalTest ? SteamUser.GetSteamID() : CSteamID.Nil;
                string myName = !IsLocalTest ? SteamFriends.GetPersonaName() : "LocalHost";
                hostSlots.SetHostSlot(myId, myName, LocalPlayerId);
            }

            // No host to remain a client of; drop role and leave the lobby.
            if (IsClient)
            {
                IsClient = false;
                hostSteamId = CSteamID.Nil;

                if (steamInitialized && currentLobbyId.IsValid())
                {
                    try { SteamMatchmaking.LeaveLobby(currentLobbyId); } catch { }
                    currentLobbyId = CSteamID.Nil;
                }
            }
        }

        private void OnDestroy()
        {
            if (IsConnected)
                Disconnect();
        }

        private void OnApplicationQuit()
        {
            if (IsConnected)
                Disconnect();
        }

        public List<FriendInfo> GetFriendsPlayingLunacid()
        {
            var friends = new List<FriendInfo>();

            if (!steamInitialized) return friends;

            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);

                FriendGameInfo_t gameInfo;
                if (SteamFriends.GetFriendGamePlayed(friendId, out gameInfo))
                {
                    if (gameInfo.m_gameID.AppID() == new AppId_t(745510)) // Lunacid AppID

                    {
                        friends.Add(new FriendInfo
                        {
                            SteamId = friendId,
                            Name = SteamFriends.GetFriendPersonaName(friendId)
                        });
                    }
                }
            }

            return friends;
        }
    }

    public struct FriendInfo
    {
        public CSteamID SteamId;
        public string Name;
    }
}