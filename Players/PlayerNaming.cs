using Steamworks;

namespace LunacidCoopMod
{
    // Display name for the local player. Steam persona if available, else "Host"/"Client" fallback.
    public static class PlayerNaming
    {
        public static string GetLocalPlayerName(bool isHost)
        {
            if (Plugin.LocalTestMode != null && Plugin.LocalTestMode.Value)
                return isHost ? "Host" : "Client";

            try
            {
                string persona = SteamFriends.GetPersonaName();
                if (!string.IsNullOrEmpty(persona)) return persona;
            }
            catch
            {
                // Steam not initialized yet; fall through to role-based default.
            }

            return isHost ? "Host" : "Client";
        }
    }
}
