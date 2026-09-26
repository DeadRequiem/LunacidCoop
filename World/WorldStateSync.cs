using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Syncs persistent world-state changes (pickups, barrels, switches, chests) via AREA_SAVED_ITEM.
    // Postfix on Save() broadcasts; receivers replay Save()+Load() so visuals and save data converge.
    public static class WorldStateSync
    {
        public static void Initialize()
        {
            SyncHandler.Subscribe<WorldStateMessage>(OnReceive);
        }

        private static void OnReceive(WorldStateMessage w)
        {
            if (!string.IsNullOrEmpty(w.Scene) && w.Scene != SceneManager.GetActiveScene().name) return;

            // Save() splices value as one character into a fixed-width zone string. 
            // Anything outside 0-9 changes its length and permanently shifts every later slot in reciever's save
            if (w.Value < 0 || w.Value > 9)
            {
                Plugin.Log.LogWarning($"[WorldSync] Rejected out-of-range value {w.Value} for Z{w.Zone}S{w.Slot}");
                return;
            }

            var items = Object.FindObjectsOfType<AREA_SAVED_ITEM>();
            int applied = 0;
            foreach (var item in items)
            {
                if (item.Zone != w.Zone || item.Slot != w.Slot) continue;

                // (Zone, Slot) isn't unique and duplicates can have different STATES sizes
                // So Load()'s STATES[value] can be out of range. 
                // Dispatch has no exception isolation.
                if (item.STATES == null || w.Value >= item.STATES.Length)
                {
                    Plugin.Log.LogWarning(
                        $"[WorldSync] Value {w.Value} exceeds STATES ({item.STATES?.Length ?? 0}) on Z{w.Zone}S{w.Slot} ({item.name}); skipping");
                    continue;
                }

                item.value = w.Value;
                Patch_AreaSavedItem_Save.ApplyingRemote = true;
                try
                {
                    item.Save();
                    item.Load();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"[WorldSync] Apply failed Z{w.Zone}S{w.Slot} ({item.name}): {e.Message}");
                }
                finally
                {
                    Patch_AreaSavedItem_Save.ApplyingRemote = false;
                }
                applied++;
            }
            CoopLog.WorldSync($"Applied state Z{w.Zone}S{w.Slot}={w.Value} ({applied} match{(applied == 1 ? "" : "es")})");
        }

        // Broadcast every AREA_SAVED_ITEM.Save() except when we're applying a remote one or the item is per-player.
        [HarmonyPatch(typeof(AREA_SAVED_ITEM), nameof(AREA_SAVED_ITEM.Save))]
        internal static class Patch_AreaSavedItem_Save
        {
            public static bool ApplyingRemote = false;

            static void Postfix(AREA_SAVED_ITEM __instance)
            {
                if (ApplyingRemote) return;
                if (!SyncHandler.IsConnected) return;

                // Per-player pickup gate: weapons/spells always private; generic items private if ItemsPerPlayer.
                if (PickupTypeGuard.CurrentPickupType >= 0)
                {
                    int t = PickupTypeGuard.CurrentPickupType;
                    bool weaponOrSpell = (t == 0 || t == 1);
                    bool genericPerPlayer = CoopConfig.ItemsPerPlayer;   // host's setting when we're a client
                    if (weaponOrSpell || genericPerPlayer)
                    {
                        Plugin.Log.LogDebug($"[WorldSync] Suppressed pickup broadcast (type={t})");
                        return;
                    }
                }

                SyncHandler.Send(new WorldStateMessage
                {
                    Scene = SceneManager.GetActiveScene().name,
                    Zone  = __instance.Zone,
                    Slot  = __instance.Slot,
                    Value = __instance.value
                });
                CoopLog.WorldSync($"Sent state Z{__instance.Zone}S{__instance.Slot}={__instance.value}");
            }
        }

        // Tracks the item type during an in-flight Item_Pickup_scr.OnTriggerEnter so the Save Postfix can gate.
        // -1 outside the window; 0 weapon, 1 spell, 2 silver, 3 item, 4 material.
        internal static class PickupTypeGuard
        {
            public static int CurrentPickupType = -1;
        }

        [HarmonyPatch(typeof(Item_Pickup_scr), "OnTriggerEnter")]
        internal static class Patch_ItemPickup_OnTriggerEnter
        {
            static void Prefix(Item_Pickup_scr __instance, Collider other)
            {
                if (other != null && other.tag == "Player")
                    PickupTypeGuard.CurrentPickupType = __instance.type;
            }

            static void Postfix()
            {
                PickupTypeGuard.CurrentPickupType = -1;
            }
        }
    }
}
