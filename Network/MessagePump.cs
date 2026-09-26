using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace LunacidCoopMod
{
    // Outgoing-side transport plumbing: outgoing queue, NPC-update coalescing, reliability/relay rules.
    // Decoupled from the byte sender via the SendBytes callback (routed to Steam P2P or LocalTransport).
    public class MessagePump
    {
        private const float BATCH_INTERVAL = 0.05f;     // 20Hz NPC-update flush
        private const int MAX_SENDS_PER_PUMP = 20;      // cap drained per frame to avoid IO stalls

        // (packet, length, target, reliable). Target is ignored in local-test mode.
        public Action<byte[], int, CSteamID, bool> SendBytes { get; set; }

        private readonly ConcurrentQueue<(NetworkMessage msg, CSteamID target)> outgoing
            = new ConcurrentQueue<(NetworkMessage, CSteamID)>();

        private readonly ConcurrentDictionary<string, NetworkMessage> pendingNpcUpdates
            = new ConcurrentDictionary<string, NetworkMessage>();

        private float lastBatchSend;

        // Queue one (message, target). NpcUpdateMessages coalesce into the batch dict instead.
        public void Enqueue(NetworkMessage msg, CSteamID target)
        {
            if (msg == null) return;

            if (msg is NpcUpdateMessage npc)
            {
                string key = $"{npc.SceneName}_{npc.NpcId}";
                pendingNpcUpdates.AddOrUpdate(key, npc, (k, existing) => npc);
                Plugin.Log.LogDebug($"[Queue Batch] NPC {npc.NpcId} HP={npc.HP}");
                return;
            }

            Plugin.Log.LogDebug($"[Queue] {msg.Kind}");
            outgoing.Enqueue((msg, target));
        }

        // Flush the coalesced NPC updates to the given targets. Called each frame past BATCH_INTERVAL.
        public void FlushNpcBatchTo(IList<CSteamID> targets)
        {
            if (pendingNpcUpdates.IsEmpty) return;
            if (Time.time - lastBatchSend < BATCH_INTERVAL) return;
            lastBatchSend = Time.time;

            if (targets == null || targets.Count == 0)
            {
                // Nowhere to send; drop so the batch doesn't accumulate forever.
                pendingNpcUpdates.Clear();
                return;
            }

            var updates = new List<NetworkMessage>(pendingNpcUpdates.Count);
            foreach (var kvp in pendingNpcUpdates) updates.Add(kvp.Value);
            pendingNpcUpdates.Clear();

            foreach (var update in updates)
                foreach (var target in targets)
                    outgoing.Enqueue((update, target));

            if (updates.Count > 0)
                Plugin.Log.LogDebug($"[Batch Send] {updates.Count} NPC updates x {targets.Count} targets");
        }

        // Drain up to MAX_SENDS_PER_PUMP queued messages, serialize, and hand bytes to SendBytes.
        public void Pump()
        {
            if (SendBytes == null) return;

            int sent = 0;
            // Cap first: TryDequeue removes the item, so testing it first drops one message per
            // capped frame, reliable ones included, since IsReliable is only read inside the body.
            while (sent < MAX_SENDS_PER_PUMP && outgoing.TryDequeue(out var item))
            {
                var (msg, target) = item;
                var packet = NetworkMessageHandler.Serialize(msg);
                if (packet == null) continue;

                SendBytes(packet, packet.Length, target, IsReliable(msg));
                sent++;
            }
        }

        // Re-send raw packet bytes to targets. Host relay path; avoids re-serialization.
        // typeRef is only used to look up reliability.
        public void RelayPacket(byte[] packet, int length, IList<CSteamID> targets, NetworkMessage typeRef)
        {
            if (SendBytes == null || packet == null || targets == null) return;

            bool reliable = IsReliable(typeRef);
            foreach (var target in targets)
            {
                SendBytes(packet, length, target, reliable);
            }
        }

        public void Clear()
        {
            while (outgoing.TryDequeue(out _)) { }
            pendingNpcUpdates.Clear();
        }

        public void ClearPendingNpcUpdates()
        {
            pendingNpcUpdates.Clear();
        }

        // --- Policy ----------

        // Messages whose loss causes visible desync go reliable, state streams stay unreliable.
        public static bool IsReliable(NetworkMessage msg)
        {
            return msg is HandshakeMessage
                || msg is LobbyStateMessage
                || msg is PingMessage
                || msg is PongMessage
                || msg is SpellCastMessage
                || msg is SpellDestroyMessage
                || msg is WorldStateMessage
                || msg is WorldDestroyMessage
                || msg is RangedFireMessage
                || msg is NpcAttackMessage
                || msg is NpcDamageMessage
                || msg is NpcPartMessage          // part deaths must not be lost
                || msg is NpcKillMessage          // a lost kill request leaves the enemy alive
                || msg is DisconnectNoticeMessage  // the whole point is that it arrives
                || msg is NpcStatusMessage        // status is edge-triggered, not a snapshot
                || msg is NpcClaimMessage         // a lost release strands the NPC until it expires
                || msg is NpcClaimReplyMessage    // a lost verdict eats the player's interaction
                || msg is HostConfigMessage       // gameplay rules; losing it desyncs both ends
                || msg is SceneQueryMessage       // single-shot and unacked; losing it strands a joiner
                || msg is ExternalChannelMessage   // cross-mod payloads; consumers can't retry
                || (msg is NpcUpdateMessage npc && npc.IsUrgent); // death / low HP
        }

        // Host-relay rule: true for client-originated messages that the host must fan out to other clients.
        public static bool IsRelayable(NetworkMessage msg)
        {
            return msg is PlayerUpdateMessage
                || msg is ChatMessage
                || msg is PlayerAttackMessage
                || msg is RangedFireMessage
                || msg is SpellCastMessage
                || msg is SpellDestroyMessage
                || msg is WorldStateMessage
                || msg is WorldDestroyMessage
                || msg is RigidbodyStateMessage
                || msg is ExternalChannelMessage;  // or a client's payload never reaches other clients
        }
    }
}
