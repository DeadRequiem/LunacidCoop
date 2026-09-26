using System;
using System.Collections.Generic;

namespace LunacidCoopMod
{
    // Application-level message dispatcher on top of SteamNetworkManager.
    // Subsystems subscribe via Subscribe<T> and send via Send.
    public static class SyncHandler
    {
        private static readonly Dictionary<Type, Action<NetworkMessage>> _handlers = new Dictionary<Type, Action<NetworkMessage>>();
        private static bool _initialized;

        public static bool IsConnected
        {
            get
            {
                var net = SteamNetworkManager.Instance;
                return net != null && net.IsConnected;
            }
        }

        public static bool IsHost
        {
            get
            {
                var net = SteamNetworkManager.Instance;
                return net != null && net.IsHost;
            }
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            var net = SteamNetworkManager.Instance;
            if (net != null)
                net.OnMessageReceived += Dispatch;

            Plugin.Log.LogInfo("[SyncHandler] Initialized");
        }

        // Register a handler for messages of type T. Multiple handlers per type are invoked in registration order.
        public static void Subscribe<T>(Action<T> handler) where T : NetworkMessage
        {
            if (handler == null) return;
            var type = typeof(T);
            Action<NetworkMessage> wrapped = m => handler((T)m);
            if (_handlers.TryGetValue(type, out var existing))
                _handlers[type] = existing + wrapped;
            else
                _handlers[type] = wrapped;
        }

        // Send a message to peers. No-op when not connected.
        public static void Send(NetworkMessage message)
        {
            var net = SteamNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.SendMessage(message);
        }

        // Per-handler isolation
        private static void Dispatch(NetworkMessage message)
        {
            if (message == null) return;
            if (!_handlers.TryGetValue(message.GetType(), out var handler) || handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<NetworkMessage>)d)(message); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[SyncHandler] Handler for {message.Kind} threw: {e}");
                }
            }
        }
    }
}
