using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Replays attack/block animations and ranged shots on the remote dummy's weapon.
    public static class WeaponSync
    {
        public static void Initialize()
        {
            SyncHandler.Subscribe<PlayerAttackMessage>(OnPlayerAttack);
            SyncHandler.Subscribe<RangedFireMessage>(OnRangedFire);
        }

        // Send the current Hand_Anim clip. Real-player filter via Player_Control_scr, dummies don't have one.
        private static void SendWeaponClip(Weapon_scr ws, string tag)
        {
            if (!SyncHandler.IsConnected) return;
            if (ws.Player == null) return;
            if (ws.Player.GetComponent<Player_Control_scr>() == null) return;

            string clipName = ws.Hand_Anim?.clip?.name;
            if (string.IsNullOrEmpty(clipName)) return;

            SyncHandler.Send(new PlayerAttackMessage
            {
                PlayerId = PlayerSyncManager.Instance?.LocalPlayerId ?? "",
                ClipName = clipName
            });
            Plugin.Log.LogDebug($"[WeaponSync] Sent {tag}: {clipName}");
        }

        private static void OnPlayerAttack(PlayerAttackMessage a)
        {
            var psm = PlayerSyncManager.Instance;
            if (psm == null) return;
            if (string.IsNullOrEmpty(a.PlayerId)) return;
            if (a.PlayerId == psm.LocalPlayerId) return;

            var dummy = psm.GetDummy(a.PlayerId);
            if (dummy == null) return;

            var visualHolder = dummy.transform.Find("Visual");
            if (visualHolder == null || visualHolder.childCount == 0) return;

            // Weapon instance is the single child of Visual.
            var weaponInstance = visualHolder.GetChild(0);
            var anim = weaponInstance.GetComponent<Animation>();
            if (anim == null) return;
            if (anim[a.ClipName] == null) return;

            anim.CrossFade(a.ClipName, 0.05f);

            // Mirror block_state/wep_state so Weapon_scr.Update() doesn't snap held block/charge poses to idle.
            // Block_Anims[0] = entering block; Attack_Anims[0] = charge windup. All other clips reset both to 0.
            var ws = weaponInstance.GetComponent<Weapon_scr>();
            if (ws != null)
            {
                bool isBlockEnter = ws.Block_Anims != null && ws.Block_Anims.Length > 0
                                 && ws.Block_Anims[0] != null
                                 && ws.Block_Anims[0].name == a.ClipName;

                bool isWindup    = ws.Attack_Anims != null && ws.Attack_Anims.Length > 0
                                 && ws.Attack_Anims[0] != null
                                 && ws.Attack_Anims[0].name == a.ClipName;

                ws.block_state = isBlockEnter ? 1 : 0;
                AccessTools.Field(typeof(Weapon_scr), "wep_state").SetValue(ws, isWindup ? 1 : 0); // wep_state is private

            }
        }

        private static void OnRangedFire(RangedFireMessage m)
        {
            var psm = PlayerSyncManager.Instance;
            if (psm == null) return;
            if (!string.IsNullOrEmpty(m.PlayerId) && m.PlayerId == psm.LocalPlayerId) return;
            // Mirrors SpellSync's CasterScene guard; without it a peer elsewhere spawns live
            // projectiles at their world coordinates inside whatever scene we are in.
            if (!string.IsNullOrEmpty(m.Scene) && m.Scene != SceneManager.GetActiveScene().name) return;

            var prefab = Resources.Load(m.ProjectileItem);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"[WeaponSync] Missing projectile prefab: {m.ProjectileItem}");
                return;
            }

            var rotation = Quaternion.LookRotation(m.Direction, Vector3.up);
            var projectile = UnityEngine.Object.Instantiate(prefab, m.Position, rotation) as GameObject;
            if (projectile == null) return;

            // Disable FloatToPlayer; it would steer the projectile back at our Camera.main.
            var ftp = projectile.GetComponent<FloatToPlayer>();
            if (ftp != null) ftp.enabled = false;

            // Same as SpellSync: a live replica per machine makes one shot land once per player.
            var dt = projectile.GetComponent<Damage_Trigger>();
            if (dt != null && m.Power > 0f) dt.power = m.Power;

            foreach (var trigger in projectile.GetComponentsInChildren<Damage_Trigger>(includeInactive: true))
                trigger.OnlyPL = true;

            Plugin.Log.LogDebug($"[WeaponSync] Spawned remote projectile: {m.ProjectileItem} from {m.Position} pow={m.Power:F1}");
        }

        // Postfix on Fire(). Stop()+Play() patterns make AnimationState weights unreliable to poll.
        [HarmonyPatch(typeof(Weapon_scr), "Fire")]
        internal static class Patch_Weapon_Fire
        {
            static void Postfix(Weapon_scr __instance) => SendWeaponClip(__instance, "attack");
        }

        // Postfix on Block(). Skip ranged weapons (they early-return without updating Hand_Anim.clip).
        [HarmonyPatch(typeof(Weapon_scr), "Block")]
        internal static class Patch_Weapon_Block
        {
            static void Postfix(Weapon_scr __instance)
            {
                if (__instance.type == 1 && __instance.special != 14) return;
                if (__instance.Block_Anims == null || __instance.Block_Anims.Length == 0) return;
                SendWeaponClip(__instance, "block");
            }
        }

        // Catches every weapon-fired projectile (ranged weapons + melee projectile specials).
        // Filters out enemy magic (NPC_MAGIC) and dummy-weapon spawners (no Player_Control_scr).
        [HarmonyPatch(typeof(Spawn_on_enable), "OnEnable")]
        internal static class Patch_Spawn_on_enable
        {
            static void Prefix(Spawn_on_enable __instance)
            {
                if (!SyncHandler.IsConnected) return;
                if (__instance.NPC_MAGIC) return;
                if (string.IsNullOrEmpty(__instance.item)) return;

                var ws = __instance.GetComponentInParent<Weapon_scr>();
                if (ws == null) return;
                if (ws.Player == null) return;
                if (ws.Player.GetComponent<Player_Control_scr>() == null) return;

                var cam = Camera.main;
                if (cam == null) return;

                // Same damage formula Spawn_on_enable applies locally: cooling scales down, +50% when fresh.
                float power = ws.WEP_DAMAGE - Mathf.Max(0f, ws.WEP_DAMAGE * (ws.cooling2 / (ws.WEP_COOLDOWN * 3f)));
                if (ws.cooling2 <= 0f) power *= 1.5f;

                SyncHandler.Send(new RangedFireMessage
                {
                    Scene          = SceneManager.GetActiveScene().name,
                    PlayerId       = PlayerSyncManager.Instance?.LocalPlayerId ?? "",
                    ProjectileItem = __instance.item,
                    Position       = cam.transform.position,
                    Direction      = cam.transform.forward,
                    Power          = power
                });
                Plugin.Log.LogDebug($"[WeaponSync] Sent ranged fire: {__instance.item} pow={power:F1}");
            }
        }
    }
}
