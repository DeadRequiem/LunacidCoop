using System;
using Steamworks;

namespace LunacidCoopMod
{
    // Gameplay settings come from the host; presentation ones (send rate, peer panel, VSync, logging) stay local.
    public static class CoopConfig
    {
        private static bool  _hasHostConfig;
        private static bool  _itemsPerPlayer   = true;
        private static bool  _scaleEnemyHp     = true;
        private static float _hpScalePerPlayer = 1f;

        // Not cleared on disconnect; gating on IsConnected means a reconnecting client keeps the host's rules.
        private static bool UseHost => _hasHostConfig && SyncHandler.IsConnected && !SyncHandler.IsHost;

        public static bool ItemsPerPlayer =>
            UseHost ? _itemsPerPlayer : (Plugin.ItemsPerPlayer == null || Plugin.ItemsPerPlayer.Value);

        public static bool ScaleEnemyHP =>
            UseHost ? _scaleEnemyHp : (Plugin.ScaleEnemyHP == null || Plugin.ScaleEnemyHP.Value);

        public static float HPScalePerPlayer =>
            UseHost ? _hpScalePerPlayer : (Plugin.HPScalePerPlayer != null ? Plugin.HPScalePerPlayer.Value : 1f);

        public static bool UsingHostConfig => UseHost;

        private static bool _initialized;

        public static void InitOnce()
        {
            if (_initialized) return;
            _initialized = true;

            SyncHandler.Subscribe<HostConfigMessage>(ApplyFromHost);

            var net = SteamNetworkManager.Instance;
            if (net != null) net.OnPeerJoined += OnPeerJoined;

            // Editing a setting mid-session must reach the clients, or the host silently changes the
            // rules for itself only.
            if (Plugin.ItemsPerPlayer   != null) Plugin.ItemsPerPlayer.SettingChanged   += OnLocalSettingChanged;
            if (Plugin.ScaleEnemyHP     != null) Plugin.ScaleEnemyHP.SettingChanged     += OnLocalSettingChanged;
            if (Plugin.HPScalePerPlayer != null) Plugin.HPScalePerPlayer.SettingChanged += OnLocalSettingChanged;

            Plugin.Log.LogInfo("[CoopConfig] Initialized");
        }

        private static HostConfigMessage Snapshot() => new HostConfigMessage
        {
            ItemsPerPlayer   = Plugin.ItemsPerPlayer   == null || Plugin.ItemsPerPlayer.Value,
            ScaleEnemyHP     = Plugin.ScaleEnemyHP     == null || Plugin.ScaleEnemyHP.Value,
            HPScalePerPlayer = Plugin.HPScalePerPlayer != null ? Plugin.HPScalePerPlayer.Value : 1f
        };

        // Host: hand one client our gameplay settings. Also called from the duplicate-handshake path,
        // which reassigns a slot without firing OnPeerJoined.
        public static void SendConfigTo(CSteamID target)
        {
            var net = SteamNetworkManager.Instance;
            if (net == null || !net.IsHost) return;
            net.SendTo(Snapshot(), target);
        }

        private static void OnPeerJoined(ClientSlot slot)
        {
            var net = SteamNetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            SendConfigTo(slot.SteamId);
            CoopLog.WorldSync($"Sent host config to slot {slot.Slot}");
        }

        // Host changed a gameplay setting mid-session; push it to everyone.
        private static void OnLocalSettingChanged(object sender, EventArgs e)
        {
            var net = SteamNetworkManager.Instance;
            if (net == null || !net.IsHost || !net.IsConnected) return;

            SyncHandler.Send(Snapshot());
            Plugin.Log.LogInfo("[CoopConfig] Gameplay setting changed; pushed to clients");
        }

        private static void ApplyFromHost(HostConfigMessage m)
        {
            if (m == null) return;

            _itemsPerPlayer   = m.ItemsPerPlayer;
            _scaleEnemyHp     = m.ScaleEnemyHP;
            _hpScalePerPlayer = m.HPScalePerPlayer;
            _hasHostConfig    = true;

            Plugin.Log.LogInfo($"[CoopConfig] Using host settings: ItemsPerPlayer={_itemsPerPlayer} " +
                               $"ScaleEnemyHP={_scaleEnemyHp} HPScalePerPlayer={_hpScalePerPlayer}");

            // HP scaling may already have been applied with our own numbers.
            AIPatches.HpScaling.RescaleAll();
        }
    }
}
