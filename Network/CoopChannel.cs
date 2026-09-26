using System;

namespace LunacidCoopMod
{
    // Public byte-channel API for other mods to send/receive bytes through the
    // coop transport without needing to register typed NetworkMessage classes
    // inside this mod. Routes everything through a single ExternalChannelMessage
    // wrapper type; consumer mods identify themselves by channel name
    // (convention: "ModName.MessageType").
    //
    // Subscribe via:
    //   CoopChannel.Received += (channel, sender, payload) => { ... };
    // Send via:
    //   CoopChannel.Broadcast("WeaponAugments.Beam", bytes);
    //
    // Consumer mods should soft-depend on this mod and guard their broadcasts
    // with CoopChannel.IsActive so single-player still works:
    //   if (CoopChannel.IsActive) CoopChannel.Broadcast(channel, bytes);
    public static class CoopChannel
    {
        // Sender is the remote player id. SyncHandler does not expose it yet; see InitOnce.
        public static event Action<string /*channel*/, byte[] /*payload*/> Received;

        public static bool IsActive => SyncHandler.IsConnected;
        public static bool IsHost   => SyncHandler.IsHost;

        private static bool _initialized;

        // Hooks the underlying typed dispatch.
        public static void InitOnce()
        {
            if (_initialized) return;
            _initialized = true;
            SyncHandler.Subscribe<ExternalChannelMessage>(OnExternalMessage);
            Plugin.Log.LogInfo("[CoopChannel] Subscribed to ExternalChannelMessage");
        }

        // Send a payload to all connected peers on the given channel name. 
        public static void Broadcast(string channel, byte[] payload)
        {
            if (!IsActive) return;
            if (string.IsNullOrEmpty(channel) || payload == null) return;
            SyncHandler.Send(new ExternalChannelMessage
            {
                Channel    = channel,
                PayloadB64 = Convert.ToBase64String(payload),
            });
        }

        private static void OnExternalMessage(ExternalChannelMessage msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.Channel)) return;
            byte[] payload;
            try { payload = Convert.FromBase64String(msg.PayloadB64 ?? ""); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[CoopChannel] Bad base64 on channel '{msg.Channel}': {e.Message}");
                return;
            }

            var handlers = Received;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<string, byte[]>)handler)(msg.Channel, payload); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[CoopChannel] Handler on '{msg.Channel}' threw: {e.Message}");
                }
            }
        }
    }
}
