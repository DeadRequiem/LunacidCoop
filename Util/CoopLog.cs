using BepInEx.Configuration;

namespace LunacidCoopMod
{
    // Channel-gated info logging. Emits only when both Plugin.EnableLogging and the per-channel toggle are true.
    // Warnings/errors bypass this - call Plugin.Log.LogWarning / LogError directly.
    public static class CoopLog
    {
        public static void CoopRigidbody(string msg)  => Emit("CoopRigidbody", msg, Plugin.LogCoopRigidbody);
        public static void SpellSync(string msg)      => Emit("SpellSync", msg, Plugin.LogSpellSync);
        public static void WeaponSync(string msg)     => Emit("WeaponSync", msg, Plugin.LogWeaponSync);
        public static void NpcScanner(string msg)     => Emit("NpcScanner", msg, Plugin.LogNpcScanner);
        public static void NpcSync(string msg)        => Emit("NpcSync", msg, Plugin.LogNpcScanner);
        public static void MPMenu(string msg)         => Emit("MPMenu", msg, Plugin.LogMPMenu);
        public static void PlayerVisuals(string msg)  => Emit("PlayerVisuals", msg, Plugin.LogPlayerVisuals);
        public static void PlayerRegistry(string msg) => Emit("PlayerRegistry", msg, Plugin.LogPlayerRegistry);
        public static void WorldSync(string msg)      => Emit("WorldSync", msg, Plugin.LogWorldSync);
        public static void NetSend(string msg)        => Emit("NET SEND", msg, Plugin.LogNetSend);
        public static void NetRecv(string msg)        => Emit("NET RECV", msg, Plugin.LogNetRecv);

        // Cheap pre-checks so callers can skip building a string at all. The net channels sit on the
        // hottest paths in the mod and their messages embed whole JSON payloads.
        public static bool NetSendEnabled => Enabled(Plugin.LogNetSend);
        public static bool NetRecvEnabled => Enabled(Plugin.LogNetRecv);

        // Set while MessageSelfCheck round-trips every message type at load. Those are probes, not
        // traffic, and would otherwise print a NET SEND line per type on every launch.
        internal static bool Suppress;

        private static bool Enabled(ConfigEntry<bool> toggle)
        {
            if (Suppress) return false;
            if (Plugin.EnableLogging != null && !Plugin.EnableLogging.Value) return false;
            return toggle == null || toggle.Value;
        }

        private static void Emit(string tag, string msg, ConfigEntry<bool> toggle)
        {
            if (Plugin.EnableLogging != null && !Plugin.EnableLogging.Value) return;
            if (toggle != null && !toggle.Value) return;
            Plugin.Log.LogInfo($"[{tag}] {msg}");
        }
    }
}
