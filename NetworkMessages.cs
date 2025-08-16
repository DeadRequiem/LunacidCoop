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
            Plugin.Log.LogDebug($"[JSON OUT] {json}");

            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] length = BitConverter.GetBytes(data.Length);

            byte[] packet = new byte[4 + data.Length];
            Array.Copy(length, 0, packet, 0, 4);
            Array.Copy(data, 0, packet, 4, data.Length);
            return packet;
        }

        public static NetworkMessage Deserialize(byte[] data)
        {
            string json = Encoding.UTF8.GetString(data);
            Plugin.Log.LogDebug($"[JSON IN] {json}");

            var env = JsonUtility.FromJson<Envelope>(json);
            if (env == null || string.IsNullOrEmpty(env.Kind)) return null;

            switch (env.Kind)
            {
                case nameof(PlayerUpdateMessage): return JsonUtility.FromJson<PlayerUpdateMessage>(env.Payload);
                case nameof(ChatMessage): return JsonUtility.FromJson<ChatMessage>(env.Payload);
                case nameof(SceneChangeMessage): return JsonUtility.FromJson<SceneChangeMessage>(env.Payload);
                case nameof(HandshakeMessage): return JsonUtility.FromJson<HandshakeMessage>(env.Payload);
                default:
                    Plugin.Log.LogWarning($"[NetworkMessage] Unknown Kind: {env.Kind}");
                    return null;
            }
        }
    }

    public class MessageBuffer
    {
        private byte[] buffer = new byte[8192];
        private int bufferPos = 0;
        private int expectedLength = -1;

        public NetworkMessage ProcessData(byte[] newData, int length)
        {
            if (bufferPos + length > buffer.Length)
            {
                Plugin.Log.LogError("[MessageBuffer] Buffer overflow!");
                Reset();
                return null;
            }

            Array.Copy(newData, 0, buffer, bufferPos, length);
            bufferPos += length;

            if (expectedLength == -1 && bufferPos >= 4)
            {
                expectedLength = BitConverter.ToInt32(buffer, 0);
                if (expectedLength < 0 || expectedLength > 4096)
                {
                    Plugin.Log.LogError($"[MessageBuffer] Invalid message length: {expectedLength}");
                    Reset();
                    return null;
                }
            }

            if (expectedLength != -1 && bufferPos >= 4 + expectedLength)
            {
                byte[] messageData = new byte[expectedLength];
                Array.Copy(buffer, 4, messageData, 0, expectedLength);

                int remaining = bufferPos - (4 + expectedLength);
                if (remaining > 0)
                    Array.Copy(buffer, 4 + expectedLength, buffer, 0, remaining);

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
        }
    }
}
