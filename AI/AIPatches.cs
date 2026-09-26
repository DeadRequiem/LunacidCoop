using HarmonyLib;
using UnityEngine;

namespace LunacidCoopMod
{
    // Patches AI_simple/AI_companion to retarget against the nearest entry in PlayerRegistry.
    static class AIPatches
    {
        static void UseNearest(AI_simple ai)
        {
            if (PlayerRegistry.Count == 0) return;
            var t = PlayerRegistry.GetNearest(ai.transform.position);
            if (t != null) ai.Player = t;
        }

        // Track() dereferences this private field; an exception there kills the coroutine for good.
        private static readonly System.Reflection.FieldInfo TargetField =
            AccessTools.Field(typeof(AI_simple), "Target");

        // Repoint NPCs off a dummy that's about to be destroyed, or Track() throws on the dead transform.
        public static void RetargetAwayFrom(Transform doomed)
        {
            if (doomed == null) return;
            var scanner = NpcScanner.Instance;
            if (scanner == null) return;

            foreach (var ai in scanner.TrackedNpcs())
            {
                if (ai == null) continue;

                bool playerDangling = ReferenceEquals(ai.Player, doomed);
                var target = TargetField?.GetValue(ai) as Transform;
                bool targetDangling = ReferenceEquals(target, doomed);
                if (!playerDangling && !targetDangling) continue;

                // Caller already unregistered it, so this can't pick it again.
                var replacement = PlayerRegistry.GetNearest(ai.transform.position);
                if (replacement == null) continue;

                if (playerDangling) ai.Player = replacement;
                if (targetDangling) TargetField.SetValue(ai, replacement);
            }

            // Remote-cast companions bind to the caster's dummy and are not in the NPC map.
            foreach (var comp in UnityEngine.Object.FindObjectsOfType<AI_companion>())
            {
                if (comp == null || !ReferenceEquals(comp.PL, doomed)) continue;
                var replacement = PlayerRegistry.GetNearestReal(comp.transform.position);
                if (replacement != null) comp.PL = replacement;
            }
        }

        static void UseNearestReal(AI_simple ai)
        {
            if (PlayerRegistry.Count == 0) return;
            var t = PlayerRegistry.GetNearestReal(ai.transform.position);
            if (t != null) ai.Player = t;
        }

        // Aggro distance check; updating Player here lets any peer trigger aggro.
        [HarmonyPatch(typeof(AI_simple), "Look")]
        static class Patch_AI_Look
        {
            static void Prefix(AI_simple __instance) => UseNearest(__instance);
        }

        // Alert() captures Player as Target, so retarget first.
        // Alert is the only thing that sets Aggressive true
        [HarmonyPatch(typeof(AI_simple), "Alert")]
        static class Patch_AI_Alert
        {
            static bool Prefix(AI_simple __instance)
            {
                if (SyncHandler.IsConnected && !SyncHandler.IsHost
                    && NpcScanner.Instance != null
                    && !string.IsNullOrEmpty(NpcScanner.Instance.GetNpcId(__instance)))
                    return false;

                UseNearest(__instance);
                return true;
            }
        }

        // TryAttack() also sets Target = Player every cycle.
        [HarmonyPatch(typeof(AI_simple), "TryAttack")]
        static class Patch_AI_TryAttack
        {
            static void Prefix(AI_simple __instance) => UseNearest(__instance);
        }

        // Die() awards XP via Player_Control_scr; use GetNearestReal so a dummy doesn't NRE the kill.
        [HarmonyPatch(typeof(AI_simple), "Die")]
        static class Patch_AI_Die
        {
            static void Prefix(AI_simple __instance) => UseNearestReal(__instance);
        }

        // Same formula runs on both sides, so HP values agree without explicit sync.
        [HarmonyPatch(typeof(AI_simple), "Start")]
        static class Patch_AI_Start_HPScale
        {
            static void Postfix(AI_simple __instance)
            {
                HpScaling.Register(__instance);
                HpScaling.Apply(__instance);
            }
        }

        internal static class HpScaling
        {
            // Unscaled max HP; rescaling off the current value would drift.
            private static readonly System.Collections.Generic.Dictionary<AI_simple, float> _originalMax
                = new System.Collections.Generic.Dictionary<AI_simple, float>();

            public static void Register(AI_simple ai)
            {
                if (ai == null) return;
                if (!_originalMax.ContainsKey(ai))
                    _originalMax[ai] = ai.health_max;
            }

            // NPC_Scaling rewrites health_max a second after Start, so the Start-time baseline is stale.
            public static void Rebaseline(AI_simple ai)
            {
                if (ai == null) return;
                float mult = CurrentMultiplier();
                if (mult <= 0f) return;
                _originalMax[ai] = ai.health_max / mult;
            }

            // Clear on scene load; AI references would be stale otherwise.
            public static void ClearAll() => _originalMax.Clear();

            public static float CurrentMultiplier()
            {
                if (!CoopConfig.ScaleEnemyHP) return 1f;
                if (!SyncHandler.IsConnected) return 1f;

                // PlayerRegistry stays empty for a moment after each scene load and counts only co-located peers.
                var net = SteamNetworkManager.Instance;
                int playerCount = Mathf.Max(2, net != null ? net.LobbyPlayerCount : 2);
                float perPlayer = CoopConfig.HPScalePerPlayer;   // host's setting on a client
                return 1f + (playerCount - 1) * perPlayer;
            }

            public static void Apply(AI_simple ai)
            {
                if (ai == null) return;
                if (!_originalMax.TryGetValue(ai, out float origMax)) return;

                float mult = CurrentMultiplier();
                float newMax = origMax * mult;

                float ratio = (ai.health_max > 0f) ? Mathf.Clamp01(ai.health / ai.health_max) : 1f;

                ai.health_max = newMax;
                ai.health = newMax * ratio;
            }

            public static void RescaleAll()
            {
                if (_originalMax.Count == 0) return;

                float mult = CurrentMultiplier();

                // Snapshot keys; destroyed AI_simple entries are pruned during iteration.
                var keys = new System.Collections.Generic.List<AI_simple>(_originalMax.Keys);
                int applied = 0;
                foreach (var ai in keys)
                {
                    if (ai == null) { _originalMax.Remove(ai); continue; }
                    Apply(ai);
                    applied++;
                }
                Plugin.Log.LogInfo($"[HPScale] Rescaled {applied} NPC(s) to x{mult:F2}");
            }
        }

        // NPCs without an NPC_Scaling never reach here and keep the Start-time baseline.
        [HarmonyPatch(typeof(NPC_Scaling), "Scale_NPC")]
        static class Patch_NpcScaling_ScaleNpc
        {
            static void Postfix(NPC_Scaling __instance)
            {
                if (__instance == null || __instance.AI == null) return;
                HpScaling.Rebaseline(__instance.AI);
            }
        }

        // Start() sets PL only when it is null, and assigns CON in that same branch.
        [HarmonyPatch(typeof(AI_companion), "Start")]
        static class Patch_Companion_Start
        {
            static void Prefix(AI_companion __instance)
            {
                if (__instance.PL != null) return;

                // RemoteOwner is set only on an inbound spell; local casts have none.
                var marker = __instance.GetComponentInParent<CoopSpellInstance>();
                if (marker == null || marker.RemoteOwner == null) return;

                // Start dereferences CON a few lines on, so bind neither unless both can be bound.
                var localPlayer = GameObject.Find("PLAYER");
                var localControl = localPlayer != null ? localPlayer.GetComponent<Player_Control_scr>() : null;
                if (localControl == null || localControl.CON == null)
                {
                    Plugin.Log.LogWarning("[AIPatches] No local CON; leaving remote companion on default binding");
                    return;
                }

                __instance.PL = marker.RemoteOwner;
                AccessTools.Field(typeof(AI_companion), "CON").SetValue(__instance, localControl.CON);

                // CON is local, so a peer's copy would otherwise drain local mana and damage NPCs twice.
                __instance.mana_reliant_drain = -1f;
                foreach (var dt in __instance.GetComponentsInChildren<Damage_Trigger>(includeInactive: true))
                    if (dt != null) dt.OnlyPL = true;

                Plugin.Log.LogInfo($"[AIPatches] Companion {__instance.name}: bound to remote owner {marker.RemoteOwner.name}");
            }
        }
    }
}
