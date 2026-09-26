using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Rigidbody sync coordinator: scene-load tagging + inbound state dispatch.
    // The per-frame ownership/broadcast loop lives on CoopRigidbody itself.
    public static class RigidbodySync
    {
        public static void Initialize()
        {
            SyncHandler.Subscribe<RigidbodyStateMessage>(OnReceive);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnReceive(RigidbodyStateMessage m)
        {
            var coop = CoopRigidbody.Find(m.Identifier);
            if (coop == null) return; // peer is in a different scene snapshot
            coop.ApplyRemoteState(m);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            CoopRigidbody.ClearAll();
            if (PlayerSyncManager.Instance != null)
                PlayerSyncManager.Instance.StartCoroutine(TagSceneRigidbodies());
        }

        // One-shot scan on scene load. Tags physics rigidbodies with sibling-index IDs.
        // Skips kinematic bodies, players, NPCs/AIs, and spell-spawned objects (those tag separately).
        private static IEnumerator TagSceneRigidbodies()
        {
            // Let the scene finish populating (some objects spawn on Start).
            yield return null;
            yield return null;

            var allRBs = Object.FindObjectsOfType<Rigidbody>();
            int tagged = 0;
            foreach (var rb in allRBs)
            {
                if (rb == null) continue;
                if (rb.isKinematic) continue;
                if (rb.GetComponent<Player_Control_scr>() != null) continue;
                if (rb.GetComponent<AI_simple>() != null) continue;
                if (rb.GetComponent<AI_companion>() != null) continue;
                if (rb.GetComponent<CoopRigidbody>() != null) continue;
                if (rb.GetComponent<CoopSpellInstance>() != null) continue;

                string id = "path:" + SceneManager.GetActiveScene().name + ":" + ScenePath.BuildScenePath(rb.transform);
                CoopRigidbody.Register(rb.gameObject, id);
                tagged++;
            }
            CoopLog.CoopRigidbody($"Tagged {tagged} scene rigidbodies in {SceneManager.GetActiveScene().name}");
        }
    }
}
