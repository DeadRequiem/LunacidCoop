using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Host-authoritative lock on vanilla talking NPCs. One player at a time is allowed into the
    // dialogue state machine; anyone else gets a prompt and no interaction.
    public class NpcSoftUnlocker : MonoBehaviour
    {
        public static NpcSoftUnlocker Instance;

        private const string BUSY_TEXT = "THIS NPC IS BUSY";

        private const int OP_REQUEST = 0;
        private const int OP_RELEASE = 1;
        private const int OP_REFRESH = 2;

        // A press while a request is still in flight is swallowed instead of resent.
        private const float REQUEST_TIMEOUT = 3f;

        // ACT fires the instant the claim lands, so a claim that has not moved
        // Current_Gameplay_State off 0 by now never will.
        private const float OPEN_GRACE = 3f;

        private const float REFRESH_INTERVAL = 5f;

        // Last resort against a release that never arrived from a peer still holding its slot.
        private const float CLAIM_EXPIRY = 20f;

        private const float PRUNE_INTERVAL = 1f;

        private struct Claim
        {
            public string Owner;
            public float LastSeen;
        }

        private struct Target
        {
            public string Scene;
            public string NpcId;
            public string Key;
        }

        // Host only. Clients keep no copy and ask for every interaction.
        private readonly Dictionary<string, Claim> claims = new Dictionary<string, Claim>();
        private float lastPrune;

        private Target held;
        private CONTROL heldControl;
        private float heldSince;
        private float lastRefresh;
        private bool sawDialogState;

        private Target pending;
        private GameObject pendingTrigger;
        private CONTROL pendingControl;
        private float pendingSince;

        private SteamNetworkManager net;

        public static void Initialize()
        {
            if (Instance != null) return;
            new GameObject("NpcSoftUnlocker").AddComponent<NpcSoftUnlocker>();
        }

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            SyncHandler.Subscribe<NpcClaimMessage>(HandleClaim);
            SyncHandler.Subscribe<NpcClaimReplyMessage>(HandleClaimReply);

            net = SteamNetworkManager.Instance;
            if (net != null)
            {
                net.OnPeerLeft += OnPeerLeft;
                net.OnDisconnected += ClearAll;
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // --- Interaction gate ----------

        // False blocks Player_Control_scr.OnActivate, so ACT never reaches the NPC.
        internal bool AllowActivate(Player_Control_scr pc)
        {
            if (pc == null || net == null) return true;
            if (!SyncHandler.IsConnected) return true;

            var trigger = pc.ACT_TRIGGER;
            if (trigger == null) return true;
            if (!IsVanillaTalkTrigger(trigger)) return true;

            // Claiming an interaction the game will refuse holds the NPC for a dialogue that never
            // opens. ACT no-ops on near, OpenMenu returns while a weapon cools, and once self-destructs.
            var act = trigger.GetComponent<Act_Button_scr>();
            if (act == null || act.once || !act.near) return true;
            if (pc.CON != null && pc.CON.EQ_WEP != null && pc.CON.EQ_WEP.cooling > 0f) return true;

            string me = net.LocalPlayerId;
            if (string.IsNullOrEmpty(me)) return true;

            string scene = SceneManager.GetActiveScene().name;
            string npcId = ScenePath.BuildScenePath(trigger.transform);

            // A trigger outside the loaded scene gets a root index of -1, which means nothing to a peer.
            if (string.IsNullOrEmpty(npcId) || npcId.StartsWith("-1")) return true;

            var target = new Target { Scene = scene, NpcId = npcId, Key = scene + "|" + npcId };
            if (target.Key == held.Key) return true;

            if (net.IsHost)
            {
                PruneExpired();
                Claim existing;
                if (claims.TryGetValue(target.Key, out existing) && existing.Owner != me)
                {
                    ShowBusy(pc.CON);
                    CoopLog.NpcSync($"Blocked talk on {target.Key}; held by {existing.Owner}");
                    return false;
                }

                claims[target.Key] = new Claim { Owner = me, LastSeen = Time.time };
                BeginHold(target, pc.CON);
                return true;
            }

            if (target.Key == pending.Key && Time.time - pendingSince < REQUEST_TIMEOUT) return false;

            pending = target;
            pendingTrigger = trigger;
            pendingControl = pc.CON;
            pendingSince = Time.time;
            SendClaimOp(target, OP_REQUEST);
            return false;
        }

        // Vanilla talk triggers are named "TALK" and route through Act_Button_scr into the menu
        // state machine; the other PreAct arms only move or enable objects.
        private static bool IsVanillaTalkTrigger(GameObject trigger)
        {
            if (trigger.name != "TALK") return false;

            var act = trigger.GetComponent<Act_Button_scr>();
            if (act == null || act.other == null) return false;

            return act.PreAct == 1 || act.PreAct == 2 || act.PreAct == 6 || act.PreAct == 7;
        }

        private static void ShowBusy(CONTROL con)
        {
            POP_text_scr pop = (con != null) ? con.PAPPY : null;
            if (pop == null)
            {
                var go = GameObject.Find("POP_TEXT");
                if (go != null) pop = go.GetComponent<POP_text_scr>();
            }
            if (pop != null) pop.POP(BUSY_TEXT, 1f, 3);
        }

        // --- Local claim lifetime ----------

        private void BeginHold(Target target, CONTROL con)
        {
            held = target;
            heldControl = con;
            heldSince = Time.time;
            lastRefresh = Time.time;
            sawDialogState = false;
            CoopLog.NpcSync($"Holding talk claim on {target.Key}");
        }

        private void ReleaseHeld(string reason)
        {
            if (held.Key == null) return;

            var target = held;
            held = default(Target);
            heldControl = null;
            sawDialogState = false;

            if (net != null && net.IsHost)
            {
                Claim existing;
                if (claims.TryGetValue(target.Key, out existing) && existing.Owner == net.LocalPlayerId)
                    claims.Remove(target.Key);
            }
            else
            {
                SendClaimOp(target, OP_RELEASE);
            }

            CoopLog.NpcSync($"Released talk claim on {target.Key} ({reason})");
        }

        private void Update()
        {
            if (net == null) return;

            if (!SyncHandler.IsConnected)
            {
                if (held.Key != null || pending.Key != null || claims.Count > 0) ClearAll();
                return;
            }

            if (net.IsHost && claims.Count > 0 && Time.time - lastPrune > PRUNE_INTERVAL)
            {
                lastPrune = Time.time;
                PruneExpired();
            }

            if (pending.Key != null && Time.time - pendingSince > REQUEST_TIMEOUT)
            {
                pending = default(Target);
                pendingTrigger = null;
                pendingControl = null;
            }

            if (held.Key == null) return;

            if (heldControl == null) { ReleaseHeld("CONTROL gone"); return; }

            int state = heldControl.Current_Gameplay_State;
            if (state != 0) sawDialogState = true;

            if (sawDialogState && state == 0) { ReleaseHeld("dialogue closed"); return; }
            if (!sawDialogState && Time.time - heldSince > OPEN_GRACE) { ReleaseHeld("dialogue never opened"); return; }

            if (Time.time - lastRefresh >= REFRESH_INTERVAL)
            {
                lastRefresh = Time.time;
                RefreshHeld();
            }
        }

        private void RefreshHeld()
        {
            if (net.IsHost)
            {
                Claim existing;
                if (claims.TryGetValue(held.Key, out existing) && existing.Owner == net.LocalPlayerId)
                    claims[held.Key] = new Claim { Owner = existing.Owner, LastSeen = Time.time };
            }
            else
            {
                SendClaimOp(held, OP_REFRESH);
            }
        }

        private void ClearAll()
        {
            claims.Clear();
            held = default(Target);
            pending = default(Target);
            heldControl = null;
            pendingControl = null;
            pendingTrigger = null;
            sawDialogState = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            pending = default(Target);
            pendingTrigger = null;
            pendingControl = null;

            if (held.Key != null) ReleaseHeld("scene change");
        }

        // --- Host table ----------

        private void PruneExpired()
        {
            if (claims.Count == 0) return;

            List<string> stale = null;
            foreach (var kvp in claims)
            {
                if (Time.time - kvp.Value.LastSeen <= CLAIM_EXPIRY) continue;
                if (stale == null) stale = new List<string>();
                stale.Add(kvp.Key);
            }
            if (stale == null) return;

            foreach (var key in stale)
            {
                claims.Remove(key);
                CoopLog.NpcSync($"Talk claim on {key} expired");
            }
        }

        private void OnPeerLeft(ClientSlot slot)
        {
            if (net == null || !net.IsHost) return;
            if (string.IsNullOrEmpty(slot.PlayerId)) return;

            List<string> owned = null;
            foreach (var kvp in claims)
            {
                if (kvp.Value.Owner != slot.PlayerId) continue;
                if (owned == null) owned = new List<string>();
                owned.Add(kvp.Key);
            }
            if (owned == null) return;

            foreach (var key in owned) claims.Remove(key);
            CoopLog.NpcSync($"Freed {owned.Count} talk claim(s) held by slot {slot.Slot}");
        }

        // --- Network ----------

        private void SendClaimOp(Target target, int op)
        {
            SyncHandler.Send(new NpcClaimMessage
            {
                Scene = target.Scene,
                NpcId = target.NpcId,
                PlayerId = net.LocalPlayerId,
                Op = op
            });
        }

        private void HandleClaim(NpcClaimMessage m)
        {
            if (net == null || !net.IsHost) return;
            if (string.IsNullOrEmpty(m.PlayerId) || string.IsNullOrEmpty(m.NpcId)) return;

            string key = m.Scene + "|" + m.NpcId;
            PruneExpired();

            if (m.Op == OP_RELEASE)
            {
                Claim owned;
                if (claims.TryGetValue(key, out owned) && owned.Owner == m.PlayerId)
                {
                    claims.Remove(key);
                    CoopLog.NpcSync($"Talk claim on {key} released by {m.PlayerId}");
                }
                return;
            }

            if (m.Op == OP_REFRESH)
            {
                Claim beat;
                if (claims.TryGetValue(key, out beat) && beat.Owner == m.PlayerId)
                    claims[key] = new Claim { Owner = beat.Owner, LastSeen = Time.time };
                return;
            }

            ClientSlot asker;
            if (!net.HostSlots.TryGetByPlayerId(m.PlayerId, out asker))
            {
                CoopLog.NpcSync($"Talk claim on {key} from unknown PlayerId {m.PlayerId}; ignoring");
                return;
            }

            Claim current;
            bool granted = !claims.TryGetValue(key, out current) || current.Owner == m.PlayerId;
            if (granted)
                claims[key] = new Claim { Owner = m.PlayerId, LastSeen = Time.time };

            net.SendTo(new NpcClaimReplyMessage { Scene = m.Scene, NpcId = m.NpcId, Granted = granted }, asker.SteamId);
            CoopLog.NpcSync($"Talk claim on {key} for slot {asker.Slot}: {(granted ? "granted" : "denied")}");
        }

        private void HandleClaimReply(NpcClaimReplyMessage r)
        {
            if (net == null || net.IsHost) return;

            string key = r.Scene + "|" + r.NpcId;
            if (key != pending.Key) return;

            var target = pending;
            var trigger = pendingTrigger;
            var con = pendingControl;
            pending = default(Target);
            pendingTrigger = null;
            pendingControl = null;

            if (!r.Granted)
            {
                ShowBusy(con);
                CoopLog.NpcSync($"Blocked talk on {key}; host denied the claim");
                return;
            }

            if (trigger == null)
            {
                SendClaimOp(target, OP_RELEASE);
                return;
            }

            BeginHold(target, con);

            // Replays the OnActivate that was blocked while the request was in flight.
            trigger.SendMessage("ACT", SendMessageOptions.DontRequireReceiver);
        }

        [HarmonyPatch(typeof(Player_Control_scr), "OnActivate")]
        internal static class Patch_PlayerControl_OnActivate
        {
            static bool Prefix(Player_Control_scr __instance)
            {
                if (Instance == null) return true;
                return Instance.AllowActivate(__instance);
            }
        }
    }
}
