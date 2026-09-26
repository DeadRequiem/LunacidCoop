using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
// Alias avoids the System.Object / UnityEngine.Object ambiguity
using Object = UnityEngine.Object;

namespace LunacidCoopMod
{
    // Spell-cast / spell-destroy networking. Patches Magic_scr.Cast to broadcast and instantiates remote copies.
    public static class SpellSync
    {
        public static void Initialize()
        {
            SyncHandler.Subscribe<SpellCastMessage>(OnSpellCast);
            SyncHandler.Subscribe<SpellDestroyMessage>(OnSpellDestroy);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // ClearAll nulls IDs first so the scene-unload OnDestroy avalanche doesn't broadcast.
            CoopSpellInstance.ClearAll();
        }

        private static void OnSpellDestroy(SpellDestroyMessage sd)
        {
            CoopSpellInstance.DestroyRemote(sd.SpellInstanceId);
        }

        private static void OnSpellCast(SpellCastMessage s)
        {
            if (string.IsNullOrEmpty(s.SpellChild)) return;
            if (!string.IsNullOrEmpty(s.CasterScene) && s.CasterScene != SceneManager.GetActiveScene().name) return;

            var prefab = Resources.Load<GameObject>($"MAGIC/CAST/{s.SpellChild}");
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"[SpellSync] Missing spell prefab: MAGIC/CAST/{s.SpellChild}");
                return;
            }

            var rotation = Quaternion.LookRotation(s.Direction);
            var spell = Object.Instantiate(prefab, s.Position, rotation);

            // FloatToPlayer homes on Camera.main and would chase the local player; disable it.
            var ftp = spell.GetComponent<FloatToPlayer>();
            if (ftp != null) ftp.enabled = false;

            // No rigidbody: parent to the caster's dummy so the spell follows them.
            // With a rigidbody: stay unparented so physics + position-sync can drive it.
            var spellRb = spell.GetComponentInChildren<Rigidbody>();
            var psm = PlayerSyncManager.Instance;
            // Falls back to primary peer dummy for messages predating the multi-client refactor.
            GameObject ownerDummy = null;
            if (psm != null)
            {
                ownerDummy = !string.IsNullOrEmpty(s.CasterPlayerId)
                    ? psm.GetDummy(s.CasterPlayerId)
                    : psm.GetPrimaryPeerDummy();
            }
            if (spellRb == null && ownerDummy != null)
            {
                spell.transform.SetParent(ownerDummy.transform, worldPositionStays: true);
            }

            // MagLife == 0 means "collision-only"; don't overwrite Auto_Kill.delay in that case.
            var autoKill = spell.GetComponent<Auto_Kill>();
            if (autoKill != null && s.MagLife > 0f)
                autoKill.delay = s.MagLife;

            // Without this, one cast damages NPCs once per machine; OnlyPL gates only the OBJ_HEALTH branch.
            var dt = spell.GetComponent<Damage_Trigger>();
            if (dt != null && s.MagDamage > 0f)
                dt.power = s.MagDamage;

            foreach (var trigger in spell.GetComponentsInChildren<Damage_Trigger>(includeInactive: true))
                trigger.OnlyPL = true;

            // Tag the spell root with the shared ID and owner. AI patches use this owner to redirect companions.
            CoopSpellInstance.Register(spell, s.SpellInstanceId, ownerDummy?.transform);

            // Register every rigidbody (some prefabs have multiple); deterministic sub-IDs via sibling-index path.
            foreach (var rb in spell.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                string subPath = ScenePath.BuildRelativePath(rb.transform, spell.transform);
                CoopRigidbody.Register(rb.gameObject, "spell:" + s.SpellInstanceId + ":" + subPath);
            }

            CoopLog.SpellSync($"Spawned remote spell: {s.SpellChild} id={s.SpellInstanceId} MagLife={s.MagLife}");
        }

        // Postfix on Magic_scr.Cast. MAG is only non-null on successful casts.
        [HarmonyPatch(typeof(Magic_scr), "Cast")]
        internal static class Patch_Magic_Cast
        {
            static void Postfix(Magic_scr __instance, GameObject ___MAG)
            {
                if (___MAG == null) return;
                if (!SyncHandler.IsConnected) return;

                // Magic_scr never clears MAG, so ___MAG can be the previous cast; the marker means it was already sent.
                if (___MAG.GetComponent<CoopSpellInstance>() != null) return;

                // Shared ID; the remote side tags its copy with the same one so destroys match.
                string instanceId = System.Guid.NewGuid().ToString();
                CoopSpellInstance.Register(___MAG, instanceId);

                foreach (var rb in ___MAG.GetComponentsInChildren<Rigidbody>(includeInactive: true))
                {
                    string subPath = ScenePath.BuildRelativePath(rb.transform, ___MAG.transform);
                    CoopRigidbody.Register(rb.gameObject, "spell:" + instanceId + ":" + subPath);
                }

                SyncHandler.Send(new SpellCastMessage
                {
                    SpellChild      = __instance.MAG_CHILD,
                    SpellInstanceId = instanceId,
                    CasterPlayerId  = PlayerSyncManager.Instance?.LocalPlayerId ?? "",
                    MagDamage       = __instance.MAG_DAMAGE,
                    MagLife         = __instance.MAG_LIFE,
                    MagType         = __instance.MAG_TYPE,
                    Position        = Camera.main.transform.position,
                    Direction       = Camera.main.transform.forward,
                    CasterScene     = SceneManager.GetActiveScene().name
                });

                CoopLog.SpellSync($"Sent spell cast: {__instance.MAG_CHILD} id={instanceId} MagLife={__instance.MAG_LIFE}");
            }
        }
    }
}
