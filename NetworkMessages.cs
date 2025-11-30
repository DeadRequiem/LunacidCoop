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
        public Vector3 Position;
        public Vector3 Rotation;
        public string WeaponName;
        public int Health;
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
    }

    [Serializable]
    public class SceneQueryMessage : NetworkMessage
    {
        public string SceneName;
    }

    [Serializable]
    public class SceneStateMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public string ZoneData;
    }

    [Serializable]
    public class NpcUpdateMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public int NpcId;
        public int HP;
        public bool Dead;

        public bool IsUrgent => Dead || HP <= 0;
    }

    // NPC Position sync message
    [Serializable]
    public class NpcPositionMessage : NetworkMessage
    {
        public string SceneName;
        public int ZoneIndex;
        public int NpcId;
        public Vector3 Position;
        public Vector3 Rotation;
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

            // Selective logging based on message type
            if (message is NpcUpdateMessage npc && (npc.Dead || npc.HP <= 10))
                Plugin.Log.LogInfo($"[NET SEND] {json}");
            else if (message is NpcPositionMessage)
                Plugin.Log.LogDebug($"[NET SEND POS] NPC {((NpcPositionMessage)message).NpcId} -> {((NpcPositionMessage)message).Position}");
            else if (!(message is NpcUpdateMessage))
                Plugin.Log.LogDebug($"[NET SEND] {json}");

            byte[] data = Encoding.UTF8.GetBytes(json);

            // Validate message size
            if (data.Length > 4096)
            {
                Plugin.Log.LogWarning($"[NET] Large message ({data.Length} bytes): {message.Kind}");
            }

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

                // Selective logging based on message type
                if (env.Kind == nameof(NpcUpdateMessage))
                {
                    var temp = JsonUtility.FromJson<NpcUpdateMessage>(env.Payload);
                    if (temp?.Dead == true || temp?.HP <= 10)
                        Plugin.Log.LogInfo($"[NET RECV] {json}");
                }
                else if (env.Kind == nameof(NpcPositionMessage))
                {
                    var temp = JsonUtility.FromJson<NpcPositionMessage>(env.Payload);
                    Plugin.Log.LogDebug($"[NET RECV POS] NPC {temp?.NpcId} -> {temp?.Position}");
                }
                else
                {
                    Plugin.Log.LogDebug($"[NET RECV] {json}");
                }

                switch (env.Kind)
                {
                    case nameof(PlayerUpdateMessage): return JsonUtility.FromJson<PlayerUpdateMessage>(env.Payload);
                    case nameof(ChatMessage): return JsonUtility.FromJson<ChatMessage>(env.Payload);
                    case nameof(SceneChangeMessage): return JsonUtility.FromJson<SceneChangeMessage>(env.Payload);
                    case nameof(HandshakeMessage): return JsonUtility.FromJson<HandshakeMessage>(env.Payload);
                    case nameof(SceneQueryMessage): return JsonUtility.FromJson<SceneQueryMessage>(env.Payload);
                    case nameof(SceneStateMessage): return JsonUtility.FromJson<SceneStateMessage>(env.Payload);
                    case nameof(NpcUpdateMessage): return JsonUtility.FromJson<NpcUpdateMessage>(env.Payload);
                    case nameof(NpcPositionMessage): return JsonUtility.FromJson<NpcPositionMessage>(env.Payload);
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
        private byte[] buffer = new byte[16384]; // Large buffer for position + health messages
        private int bufferPos = 0;
        private int expectedLength = -1;

        public NetworkMessage ProcessData(byte[] newData, int length)
        {
            // Prevent buffer overflow
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

            // Try to read message length header
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

            // Try to read complete message
            if (expectedLength != -1 && bufferPos >= 4 + expectedLength)
            {
                byte[] messageData = new byte[expectedLength];
                Array.Copy(buffer, 4, messageData, 0, expectedLength);

                // Shift remaining data to beginning of buffer
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