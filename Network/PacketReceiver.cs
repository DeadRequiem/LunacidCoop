using System;
using System.Collections.Generic;
using Steamworks;

namespace LunacidCoopMod
{
    // Steam P2P receive path. One MessageBuffer per sender so peers can't corrupt each other's framing.
    public class PacketReceiver
    {
        private readonly Dictionary<CSteamID, MessageBuffer> buffersBySender = new Dictionary<CSteamID, MessageBuffer>();

        // Fired per complete parsed message. Args: parsed message, sender, raw packet bytes, byte count.
        // Raw bytes are passed so the host can relay verbatim without re-serializing.
        public event Action<NetworkMessage, CSteamID, byte[], int> OnMessageParsed;

        // Drain all available P2P packets. Call once per Update.
        public void Pump()
        {
            uint packetSize;
            while (SteamNetworking.IsP2PPacketAvailable(out packetSize))
            {
                byte[] packetData = new byte[packetSize];
                uint bytesRead;
                CSteamID senderId;

                if (!SteamNetworking.ReadP2PPacket(packetData, packetSize, out bytesRead, out senderId))
                    continue;

                Plugin.Log.LogDebug($"[Steam] Received {bytesRead} bytes from {senderId}");

                var buffer = GetOrCreateBuffer(senderId);
                var msg = buffer.ProcessData(packetData, (int)bytesRead);
                while (msg != null)
                {
                    OnMessageParsed?.Invoke(msg, senderId, packetData, (int)bytesRead);
                    msg = buffer.ProcessData(new byte[0], 0);
                }
            }
        }

        // Fetch or create the framing buffer for a sender. Also called on session accept to pre-init.
        public MessageBuffer GetOrCreateBuffer(CSteamID sender)
        {
            if (!buffersBySender.TryGetValue(sender, out var buf))
            {
                buf = new MessageBuffer();
                buffersBySender[sender] = buf;
            }
            return buf;
        }

        // Drop a disconnected peer's buffer so stale framing state can't survive leave/rejoin.
        public void RemoveBuffer(CSteamID sender)
        {
            buffersBySender.Remove(sender);
        }

        public void Clear()
        {
            buffersBySender.Clear();
        }
    }
}
