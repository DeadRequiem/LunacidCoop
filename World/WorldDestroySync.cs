using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Syncs OBJ_HEALTH.Die() for non-NPC breakables. Spell-owned objects route through SpellDestroyMessage.
    public static class WorldDestroySync
    {
        private static readonly Dictionary<string, GameObject> pathToObj = new Dictionary<string, GameObject>();
        private static readonly Dictionary<GameObject, string> objToPath = new Dictionary<GameObject, string>();

        public static void Initialize()
        {
            SyncHandler.Subscribe<WorldDestroyMessage>(OnReceive);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            pathToObj.Clear();
            objToPath.Clear();

            if (PlayerSyncManager.Instance != null)
                PlayerSyncManager.Instance.StartCoroutine(IndexDestructibles(scene));
        }

        private static IEnumerator IndexDestructibles(Scene scene)
        {
            yield return null;
            yield return null;

            foreach (var oh in Object.FindObjectsOfType<OBJ_HEALTH>(true))
            {
                if (oh == null) continue;
                if (oh.type == 1 || oh.type == 2) continue;   // NPC bodies sync via NpcScanner

                string path = ScenePath.BuildScenePath(oh.transform);
                if (string.IsNullOrEmpty(path) || pathToObj.ContainsKey(path)) continue;

                pathToObj[path] = oh.gameObject;
                objToPath[oh.gameObject] = path;
            }
            CoopLog.WorldSync($"Indexed {pathToObj.Count} destructibles in {scene.name}");
        }

        // Scan-time path for an object, falling back to a live build for anything spawned later.
        private static string PathFor(GameObject go)
        {
            if (go != null && objToPath.TryGetValue(go, out string p)) return p;
            return ScenePath.BuildScenePath(go != null ? go.transform : null);
        }

        private static void OnReceive(WorldDestroyMessage w)
        {
            if (!string.IsNullOrEmpty(w.Scene) && w.Scene != SceneManager.GetActiveScene().name) return;

            // Registry first; live resolution only for objects that appeared after the scan.
            if (!pathToObj.TryGetValue(w.ObjectPath, out var go) || go == null)
                go = ScenePath.ResolveScenePath(w.ObjectPath, SceneManager.GetActiveScene());
            if (go == null)
            {
                Plugin.Log.LogWarning($"[WorldSync] Could not resolve path '{w.ObjectPath}' in {w.Scene}");
                return;
            }

            var oh = go.GetComponent<OBJ_HEALTH>();
            if (oh == null)
            {
                // No OBJ_HEALTH; plain destroy fallback.
                Object.Destroy(go);
                CoopLog.WorldSync($"Destroyed (fallback) '{w.ObjectPath}'");
                return;
            }

            Patch_ObjHealth_Die.ApplyingRemote = true;
            try { oh.Die(); }
            finally { Patch_ObjHealth_Die.ApplyingRemote = false; }
            CoopLog.WorldSync($"Applied Die() to '{w.ObjectPath}'");
        }

        [HarmonyPatch(typeof(OBJ_HEALTH), nameof(OBJ_HEALTH.Die))]
        internal static class Patch_ObjHealth_Die
        {
            public static bool ApplyingRemote = false;

            static void Prefix(OBJ_HEALTH __instance)
            {
                if (ApplyingRemote) return;
                if (__instance.type == 1 || __instance.type == 2) return; // NPC body; skip
                if (!SyncHandler.IsConnected) return;

                // Spell-owned death: destroy the full spell root on both sides via SpellDestroyMessage.
                var spell = __instance.GetComponentInParent<CoopSpellInstance>();
                if (spell != null && !string.IsNullOrEmpty(spell.SpellInstanceId))
                {
                    SyncHandler.Send(new SpellDestroyMessage { SpellInstanceId = spell.SpellInstanceId });
                    CoopSpellInstance.SuppressNextBroadcast(spell.SpellInstanceId);
                    Object.Destroy(spell.gameObject);
                    CoopLog.WorldSync($"Spell-instance destroy via OBJ_HEALTH: id={spell.SpellInstanceId}");
                    return;
                }

                // Plain destructible: broadcast its hierarchy path.
                string path = PathFor(__instance.gameObject);
                SyncHandler.Send(new WorldDestroyMessage
                {
                    Scene = SceneManager.GetActiveScene().name,
                    ObjectPath = path
                });
                CoopLog.WorldSync($"Sent destroy '{path}' (type={__instance.type}, dest={__instance.dest_type})");
            }
        }
    }
}
