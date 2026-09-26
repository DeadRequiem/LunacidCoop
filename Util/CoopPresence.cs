using System;
using Steamworks;
using UnityEngine;

namespace LunacidCoopMod
{
    // Lunacid has no rich presence localization file, so steam_display and the status key render
    // nothing in the friends list. steam_player_group and its size are the exception: Steam draws the
    // grouping itself, with no token in the app.
    public static class CoopPresence
    {
        private const string GroupKey = "steam_player_group";
        private const string GroupSizeKey = "steam_player_group_size";
        private const float RefreshInterval = 1f;

        private static bool initialized;
        private static bool pending;
        private static float lastApply;

        private static string lastGroup = "";
        private static string lastGroupSize = "";

        public static bool InSession
        {
            get
            {
                var net = SteamNetworkManager.Instance;
                return net != null && (net.IsConnected || net.IsHost || net.IsClient);
            }
        }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            var net = SteamNetworkManager.Instance;
            if (net != null)
            {
                net.OnConnected += Refresh;
                net.OnDisconnected += Refresh;
                net.OnPeerJoined += OnRosterChanged;
                net.OnPeerLeft += OnRosterChanged;
            }
            else
            {
                Plugin.Log.LogWarning("[Presence] No SteamNetworkManager at init; presence will not follow the session");
            }

            var go = new GameObject("CoopPresence");
            go.AddComponent<PresenceTicker>();
            UnityEngine.Object.DontDestroyOnLoad(go);

            Plugin.Log.LogInfo("[Presence] Initialized");
        }

        public static void Refresh()
        {
            pending = true;
        }

        // Cleanup wipes every key behind this cache, so the next compare could match and write nothing.
        public static void Invalidate()
        {
            lastGroup = null;
            lastGroupSize = null;
        }

        private static void OnRosterChanged(ClientSlot slot)
        {
            pending = true;
        }

        private static void Apply()
        {
            var net = SteamNetworkManager.Instance;
            if (net == null || !net.SteamReady) return;

            int count = net.LobbyPlayerCount;
            bool grouped = InSession && count > 1 && net.HostSteamId.IsValid();

            // Every member derives the same key: the host knows its own id, a client knows the host's.
            string group = grouped ? net.HostSteamId.m_SteamID.ToString() : "";
            string size = grouped ? count.ToString() : "";
            if (group == lastGroup && size == lastGroupSize) return;

            try
            {
                SteamFriends.SetRichPresence(GroupKey, group);
                SteamFriends.SetRichPresence(GroupSizeKey, size);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Presence] SetRichPresence threw: {e.Message}");
                return;
            }

            lastGroup = group;
            lastGroupSize = size;
            Plugin.Log.LogInfo($"[Presence] group = '{group}' size = '{size}'");
        }

        // Triggers only raise a flag. Applying from Update reads the roster after the transport has
        // finished mutating it, which matters because OnPeerLeft fires mid-teardown.
        private sealed class PresenceTicker : MonoBehaviour
        {
            private void Update()
            {
                if (!pending) return;
                if (Time.unscaledTime - lastApply < RefreshInterval) return;

                pending = false;
                lastApply = Time.unscaledTime;
                Apply();
            }
        }
    }
}
