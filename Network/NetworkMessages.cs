using System;
using System.Text;
using UnityEngine;

namespace LunacidCoopMod
{
    [Serializable]
    public abstract class NetworkMessage
    {
        public string Kind => GetType().Name;
        public float Timestamp = Time.time;
    }

    [Serializable]
    public class PlayerUpdateMessage : NetworkMessage
    {
        public string PlayerId;
        public string PlayerName;
        public Vector3 Position;
        public Quaternion Rotation;
        public string WeaponName;
        public int Health;
        // Health alone is meaningless to a peer: PLAYER_MAX_HP is per-character. 0 = not supplied.
        public int HealthMax;
        public string CurrentScene;
    }

    [Serializable]
    public class ChatMessage : NetworkMessage
    {
        public string PlayerName;
        public string Content;
    }

    [Serializable]
    public class SceneChangeMessage : NetworkMessage
    {
        public string SceneName;
        public Vector3 SpawnPosition;
    }

    [Serializable]
    public class HandshakeMessage : NetworkMessage
    {
        public string PlayerName;
        public string ModVersion;

        // Client->host: PlayerId is the client's GUID, AssignedSlot unused.
        // Host->client: PlayerId is host's GUID, AssignedSlot is the client's slot (1..3).
        public string PlayerId;
        public int AssignedSlot = -1;

        // Bumped by hand only when the wire format changes, unlike ModVersion which tracks releases.
        // Compared strictly: a mismatch means the two builds disagree about message types.
        public int ProtocolVersion;
    }

    // Host -> client refusal or goodbye, and client -> host "I am leaving". Without it a host that
    // quits is indistinguishable from a stall, and a refused client just retries into silence.
    [Serializable]
    public class DisconnectNoticeMessage : NetworkMessage
    {
        public string Reason;
    }

    [Serializable]
    public class SceneQueryMessage : NetworkMessage
    {
        public string SceneName;
        // Asker's PlayerId so host responds only to them, not all clients.
        public string AskerPlayerId;
    }

    // Host-authoritative lobby snapshot. Parallel primitive arrays (JsonUtility drops array-of-class fields).
    [Serializable]
    public class LobbyStateMessage : NetworkMessage
    {
        public int[]    SlotIndices;
        public string[] SteamIds;       // decimal CSteamID.m_SteamID; "0" for local-test entries
        public string[] PlayerNames;
        public string[] PlayerIds;
        public float[]  JoinTimes;
    }

    // Host broadcast of each client's measured RTT, so clients can show peer pings. Excludes the host's own slot.
    [Serializable]
    public class PingTableMessage : NetworkMessage
    {
        public string[] PlayerIds;
        public float[]  PingsMs;
    }

    [Serializable]
    public class SceneStateMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public string ZoneData;
    }

    // NpcId is a sibling-index path from the scene root (CoopRigidbody scheme); stable across machines.
    [Serializable]
    public class NpcUpdateMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public string NpcId;
        public int HP;
        public bool Dead;

        // Host-authoritative max; each machine's NPC_Scaling otherwise derives its own from the local
        // save, so bar fill and the limp threshold disagree. 0 = not supplied, leave the local value.
        public float HealthMax;

        // Set only on the join/scene-change catch-up burst, so a client can tell a real snapshot
        // from ordinary combat traffic when deciding whether its request was answered.
        public bool Snapshot;

        // AI_simple.UNDETH comes from PlayerPrefs at Start, so it is per-machine. Mirror the host's
        // value or a client can believe an unkillable enemy is killable.
        public bool Undeth;

        public bool IsUrgent => Dead || HP <= 0;
    }

    // Per-part health for multi-part NPCs; AI_simple.health is the body total only.
    // PartPath is a sibling-index path relative to the AI_simple root.
    [Serializable]
    public class NpcPartMessage : NetworkMessage
    {
        public string SceneName;
        public string NpcId;
        public string PartPath;
        public float Health;
        public bool Dead;
    }

    // Client -> host kill request. Weapon specials call AI_simple.Die() directly, bypassing
    // OBJ_HEALTH.Hurt entirely, so a client would otherwise delete a host-owned enemy locally.
    [Serializable]
    public class NpcKillMessage : NetworkMessage
    {
        public string Scene;
        public string NpcId;
    }

    // Host -> client elemental status. temperature/poi accumulate inside OBJ_HEALTH.Hurt, which the
    // client never runs, so fire/ice/poison effects would never appear there.
    // Which: 0 fire, 1 ice, 2 poison. PartPath is relative to the AI_simple root.
    [Serializable]
    public class NpcStatusMessage : NetworkMessage
    {
        public string Scene;
        public string NpcId;
        public string PartPath;
        public int Which;
    }

    // Client -> host talking-NPC lock traffic. NpcId is the trigger's sibling-index path.
    // Op: 0 request, 1 release, 2 refresh.
    [Serializable]
    public class NpcClaimMessage : NetworkMessage
    {
        public string Scene;
        public string NpcId;
        public string PlayerId;
        public int Op;
    }

    // Host -> requester verdict on one claim request. The host table is the only authority.
    [Serializable]
    public class NpcClaimReplyMessage : NetworkMessage
    {
        public string Scene;
        public string NpcId;
        public bool Granted;
    }

    [Serializable]
    public class NpcPositionMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public string NpcId;
        public Vector3 Position;
        public Vector3 Rotation;
        public string ClipName;
        public float ClipSpeed;
    }

    [Serializable]
    public class NpcAttackMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public string NpcId;
        public int BehaviorIndex;
    }

    // Client->host damage report. Client suppresses its local Hurt and lets the host's NpcUpdate propagate HP back.
    [Serializable]
    public class NpcDamageMessage : NetworkMessage
    {
        public string Scene;
        public string NpcId;
        public float Amount;
        public int Element;             // 0 Normal, 1 Fire, 2 Ice, 3 Poison, 4 Light, 5 Dark
        public string AttackerPlayerId; // reserved for future XP/kill-credit routing

        // Which OBJ_HEALTH was hit. Without it the host applies every report to the first child, so
        // the wrong part takes damage and a type-2 death disables the wrong attack. Empty = unknown.
        public string PartPath;
    }

    [Serializable]
    public class PlayerAttackMessage : NetworkMessage
    {
        public string PlayerId;
        public string ClipName;
    }

    // Ranged weapon fire. Remote side instantiates the projectile directly because the
    // local Spawn_on_enable uses Camera.main and would aim at the receiver.
    [Serializable]
    public class RangedFireMessage : NetworkMessage
    {
        public string Scene;            // without this, peers spawn live projectiles into your scene
        public string PlayerId;
        public string ProjectileItem;  // Resources path (Spawn_on_enable.item)
        public Vector3 Position;
        public Vector3 Direction;
        public float Power;
    }

    [Serializable]
    public class SpellCastMessage : NetworkMessage
    {
        public string SpellChild;       // MAG_CHILD, Resources path under MAGIC/CAST/
        public string SpellInstanceId;  // shared ID stamped on both sides' spawned copies
        public string CasterPlayerId;
        public float MagDamage;
        public float MagLife;
        public int MagType;
        public Vector3 Position;
        public Vector3 Direction;
        public string CasterScene;
    }

    [Serializable]
    public class SpellDestroyMessage : NetworkMessage
    {
        public string SpellInstanceId;
    }

    // AREA_SAVED_ITEM state. (Scene, Zone, Slot) resolves to the same component on both ends.
    [Serializable]
    public class WorldStateMessage : NetworkMessage
    {
        public string Scene;
        public int Zone;
        public int Slot;
        public int Value;
    }

    // Destruction of an OBJ_HEALTH-only object, identified by sibling-index path in the scene.
    [Serializable]
    public class WorldDestroyMessage : NetworkMessage
    {
        public string Scene;
        public string ObjectPath;
    }

    // Identifier prefixes: "path:<scenepath>" (scene-placed) or "spell:<guid>" (spell-summoned).
    [Serializable]
    public class RigidbodyStateMessage : NetworkMessage
    {
        public string Identifier;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    [Serializable]
    public class PingMessage : NetworkMessage
    {
        public float RequestTime;
    }

    [Serializable]
    public class PongMessage : NetworkMessage
    {
        public float RequestTime;
    }

    // Generic channel envelope for cross-mod broadcasting. 
    // This is mostly for my other mods (like WeaponAugments) for H+C anim sync
    // See CoopChannel for the wrapper API consumer mods use.
    [Serializable]
    public class ExternalChannelMessage : NetworkMessage
    {
        public string Channel;
        public string PayloadB64;
    }

    // Gameplay settings are the host's. Sent to each client as it joins so both ends apply the same
    // rules; a mismatch otherwise writes items into a save whose owner asked for private pickups.
    [Serializable]
    public class HostConfigMessage : NetworkMessage
    {
        public bool ItemsPerPlayer;
        public bool ScaleEnemyHP;
        public float HPScalePerPlayer;
    }

    [Serializable]
    internal class Envelope
    {
        public string Kind;
        public string Payload;
    }

    public static class NetworkMessageHandler
    {
        public static byte[] Serialize(NetworkMessage message)
        {
            var env = new Envelope
            {
                Kind = message.GetType().Name,
                Payload = JsonUtility.ToJson(message, false)
            };

            string json = JsonUtility.ToJson(env, false);

            // Behind the channel toggle, so nothing is interpolated while it is off.
            if (CoopLog.NetSendEnabled)
            {
                if (message is NpcUpdateMessage npc)
                {
                    if (npc.Dead || npc.HP <= 10) CoopLog.NetSend(json);
                }
                else if (message is NpcAttackMessage atk)
                {
                    CoopLog.NetSend("Attack NPC " + atk.NpcId + " behavior " + atk.BehaviorIndex);
                }
                else if (message is NpcPositionMessage pos)
                {
                    CoopLog.NetSend("POS NPC " + pos.NpcId + " -> " + pos.Position);
                }
                else if (!(message is PingMessage) && !(message is PongMessage))
                {
                    CoopLog.NetSend(json);
                }
            }

            byte[] data = Encoding.UTF8.GetBytes(json);

            if (data.Length > 4096)
                Plugin.Log.LogWarning($"[NET] Large message ({data.Length} bytes): {message.Kind}");

            byte[] length = BitConverter.GetBytes(data.Length);

            byte[] packet = new byte[4 + data.Length];
            Array.Copy(length, 0, packet, 0, 4);
            Array.Copy(data, 0, packet, 4, data.Length);
            return packet;
        }

        public static NetworkMessage Deserialize(byte[] data)
        {
            try
            {
                string json = Encoding.UTF8.GetString(data);

                var env = JsonUtility.FromJson<Envelope>(json);
                if (env == null || string.IsNullOrEmpty(env.Kind)) return null;

                // Selective per-type recv logging, all behind the channel toggle. This used to parse
                // the payload a SECOND time purely to log it, on the hottest receive path.
                if (CoopLog.NetRecvEnabled)
                {
                    if (env.Kind == nameof(NpcUpdateMessage))
                    {
                        var temp = JsonUtility.FromJson<NpcUpdateMessage>(env.Payload);
                        if (temp != null && (temp.Dead || temp.HP <= 10)) CoopLog.NetRecv(json);
                    }
                    else if (env.Kind != nameof(PingMessage) && env.Kind != nameof(PongMessage))
                    {
                        CoopLog.NetRecv(json);
                    }
                }

                switch (env.Kind)
                {
                    case nameof(PlayerUpdateMessage): return JsonUtility.FromJson<PlayerUpdateMessage>(env.Payload);
                    case nameof(ChatMessage): return JsonUtility.FromJson<ChatMessage>(env.Payload);
                    case nameof(SceneChangeMessage): return JsonUtility.FromJson<SceneChangeMessage>(env.Payload);
                    case nameof(HandshakeMessage): return JsonUtility.FromJson<HandshakeMessage>(env.Payload);
                    case nameof(SceneQueryMessage): return JsonUtility.FromJson<SceneQueryMessage>(env.Payload);
                    case nameof(SceneStateMessage): return JsonUtility.FromJson<SceneStateMessage>(env.Payload);
                    case nameof(LobbyStateMessage): return JsonUtility.FromJson<LobbyStateMessage>(env.Payload);
                    case nameof(PingTableMessage):  return JsonUtility.FromJson<PingTableMessage>(env.Payload);
                    case nameof(NpcUpdateMessage): return JsonUtility.FromJson<NpcUpdateMessage>(env.Payload);
                    case nameof(NpcPositionMessage): return JsonUtility.FromJson<NpcPositionMessage>(env.Payload);
                    case nameof(NpcAttackMessage): return JsonUtility.FromJson<NpcAttackMessage>(env.Payload);
                    case nameof(NpcDamageMessage): return JsonUtility.FromJson<NpcDamageMessage>(env.Payload);
                    case nameof(NpcPartMessage): return JsonUtility.FromJson<NpcPartMessage>(env.Payload);
                    case nameof(NpcKillMessage): return JsonUtility.FromJson<NpcKillMessage>(env.Payload);
                    case nameof(DisconnectNoticeMessage): return JsonUtility.FromJson<DisconnectNoticeMessage>(env.Payload);
                    case nameof(NpcStatusMessage): return JsonUtility.FromJson<NpcStatusMessage>(env.Payload);
                    case nameof(NpcClaimMessage): return JsonUtility.FromJson<NpcClaimMessage>(env.Payload);
                    case nameof(NpcClaimReplyMessage): return JsonUtility.FromJson<NpcClaimReplyMessage>(env.Payload);
                    case nameof(HostConfigMessage): return JsonUtility.FromJson<HostConfigMessage>(env.Payload);
                    case nameof(PlayerAttackMessage): return JsonUtility.FromJson<PlayerAttackMessage>(env.Payload);
                    case nameof(RangedFireMessage): return JsonUtility.FromJson<RangedFireMessage>(env.Payload);
                    case nameof(SpellCastMessage): return JsonUtility.FromJson<SpellCastMessage>(env.Payload);
                    case nameof(SpellDestroyMessage): return JsonUtility.FromJson<SpellDestroyMessage>(env.Payload);
                    case nameof(WorldStateMessage): return JsonUtility.FromJson<WorldStateMessage>(env.Payload);
                    case nameof(WorldDestroyMessage): return JsonUtility.FromJson<WorldDestroyMessage>(env.Payload);
                    case nameof(RigidbodyStateMessage): return JsonUtility.FromJson<RigidbodyStateMessage>(env.Payload);
                    case nameof(PingMessage): return JsonUtility.FromJson<PingMessage>(env.Payload);
                    case nameof(PongMessage): return JsonUtility.FromJson<PongMessage>(env.Payload);
                    case nameof(ExternalChannelMessage): return JsonUtility.FromJson<ExternalChannelMessage>(env.Payload);
                    default:
                        Plugin.Log.LogWarning($"[NetworkMessage] Unknown Kind: {env.Kind}");
                        return null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[NetworkMessage] Deserialize error: {e.Message}");
                return null;
            }
        }
    }

    public class MessageBuffer
    {
        private byte[] buffer = new byte[16384];
        private int bufferPos = 0;
        private int expectedLength = -1;

        public NetworkMessage ProcessData(byte[] newData, int length)
        {
            if (bufferPos + length > buffer.Length)
            {
                Plugin.Log.LogError($"[MessageBuffer] Buffer overflow! Current: {bufferPos}, New: {length}, Max: {buffer.Length}");
                Reset();
                return null;
            }

            if (length > 0)
            {
                Array.Copy(newData, 0, buffer, bufferPos, length);
                bufferPos += length;
            }

            // Read length prefix once available.
            if (expectedLength == -1 && bufferPos >= 4)
            {
                expectedLength = BitConverter.ToInt32(buffer, 0);
                if (expectedLength < 0 || expectedLength > 8192)
                {
                    Plugin.Log.LogError($"[MessageBuffer] Invalid message length: {expectedLength}");
                    Reset();
                    return null;
                }
            }

            // Extract one complete message once enough bytes are buffered.
            if (expectedLength != -1 && bufferPos >= 4 + expectedLength)
            {
                byte[] messageData = new byte[expectedLength];
                Array.Copy(buffer, 4, messageData, 0, expectedLength);

                // Shift leftover bytes back to the start of the buffer.
                int remaining = bufferPos - (4 + expectedLength);
                if (remaining > 0)
                {
                    Array.Copy(buffer, 4 + expectedLength, buffer, 0, remaining);
                }

                bufferPos = remaining;
                expectedLength = -1;

                return NetworkMessageHandler.Deserialize(messageData);
            }

            return null;
        }

        public void Reset()
        {
            bufferPos = 0;
            expectedLength = -1;
            Plugin.Log.LogDebug("[MessageBuffer] Reset");
        }
    }
}