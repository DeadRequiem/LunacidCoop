using System.Collections.Generic;
using Steamworks;

namespace LunacidCoopMod
{
    // Per-peer liveness + round-trip ping tracking, used by the ping loop and timeout watchdog.
    // Host tracks each client independently; client tracks the single host peer.
    public class PingTracker
    {
        // Host-side per-client state.
        private readonly Dictionary<CSteamID, float> lastPongPerClient = new Dictionary<CSteamID, float>();
        private readonly Dictionary<CSteamID, float> pingPerClient     = new Dictionary<CSteamID, float>();

        // Client-side single-host state.
        private float hostLastPong;
        private float hostPingMs;

        // --- Host side ----------

        // Record a pong from a client: receipt time for the watchdog, RTT for the HUD.
        public void RecordPongFromClient(CSteamID clientId, float nowSeconds, float roundTripMs)
        {
            lastPongPerClient[clientId] = nowSeconds;
            pingPerClient[clientId]     = roundTripMs;
        }

        // Non-pong liveness signal (e.g. an incoming ping from them proves they're alive).
        public void MarkClientAlive(CSteamID clientId, float nowSeconds)
        {
            lastPongPerClient[clientId] = nowSeconds;
        }

        // Seed liveness for a fresh client. Without this the watchdog would trip immediately on first tick.
        public void SeedClient(CSteamID clientId, float nowSeconds)
        {
            lastPongPerClient[clientId] = nowSeconds;
            if (!pingPerClient.ContainsKey(clientId)) pingPerClient[clientId] = 0f;
        }

        public void RemoveClient(CSteamID clientId)
        {
            lastPongPerClient.Remove(clientId);
            pingPerClient.Remove(clientId);
        }

        public float GetClientPingMs(CSteamID clientId)
        {
            return pingPerClient.TryGetValue(clientId, out float p) ? p : 0f;
        }

        // Return any client whose last-pong is older than (now - timeout). Only checks the supplied set.
        public List<CSteamID> FindTimedOutClients(IEnumerable<CSteamID> activeClients, float nowSeconds, float timeoutSeconds)
        {
            List<CSteamID> dead = null;
            foreach (var id in activeClients)
            {
                float last = lastPongPerClient.TryGetValue(id, out float t) ? t : 0f;
                if (nowSeconds - last > timeoutSeconds)
                {
                    if (dead == null) dead = new List<CSteamID>();
                    dead.Add(id);
                }
            }
            return dead;
        }

        // --- Client side ----------

        public void RecordPongFromHost(float nowSeconds, float roundTripMs)
        {
            hostLastPong = nowSeconds;
            hostPingMs   = roundTripMs;
        }

        public void MarkHostAlive(float nowSeconds)
        {
            hostLastPong = nowSeconds;
        }

        public float HostPingMs => hostPingMs;

        public bool IsHostTimedOut(float nowSeconds, float timeoutSeconds)
        {
            return nowSeconds - hostLastPong > timeoutSeconds;
        }

        // --- Lifecycle ----------

        public void Clear()
        {
            lastPongPerClient.Clear();
            pingPerClient.Clear();
            hostLastPong = 0f;
            hostPingMs   = 0f;
        }
    }
}
