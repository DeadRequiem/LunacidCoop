using System.Collections.Generic;
using UnityEngine;

namespace LunacidCoopMod
{
    // Marker on every spell-spawned GameObject (both ends). Shared GUID lets destruction events match across the wire.
    public class CoopSpellInstance : MonoBehaviour
    {
        public string SpellInstanceId;

        // Set on remote-spawned spells to the caster's dummy transform; null for local casts.
        // AI patches use this to redirect companion targets; positional heuristics aren't reliable at Start.
        public Transform RemoteOwner;

        private static readonly Dictionary<string, CoopSpellInstance> _active = new Dictionary<string, CoopSpellInstance>();
        private static readonly HashSet<string> _suppressBroadcast = new HashSet<string>();

        public static void Register(GameObject go, string id, Transform remoteOwner = null)
        {
            if (go == null || string.IsNullOrEmpty(id)) return;
            var c = go.AddComponent<CoopSpellInstance>();
            c.SpellInstanceId = id;
            c.RemoteOwner = remoteOwner;
            _active[id] = c;
        }

        // Apply an inbound SpellDestroyMessage. No-op for unknown IDs.
        public static void DestroyRemote(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (_active.TryGetValue(id, out var c) && c != null)
            {
                _suppressBroadcast.Add(id);
                Object.Destroy(c.gameObject);
            }
        }

        // Make the next OnDestroy for this ID silent. Use when we've already sent the destroy ourselves.
        public static void SuppressNextBroadcast(string id)
        {
            if (!string.IsNullOrEmpty(id)) _suppressBroadcast.Add(id);
        }

        // Wipe the registry on scene change; null IDs first so scene-unload OnDestroy doesn't flood the wire.
        public static void ClearAll()
        {
            foreach (var c in _active.Values)
            {
                if (c != null) c.SpellInstanceId = null;
            }
            _active.Clear();
            _suppressBroadcast.Clear();
        }

        private void OnDestroy()
        {
            if (string.IsNullOrEmpty(SpellInstanceId)) return;
            _active.Remove(SpellInstanceId);

            // Don't echo a remote-initiated destroy back.
            if (_suppressBroadcast.Remove(SpellInstanceId)) return;

            if (!SyncHandler.IsConnected) return;
            SyncHandler.Send(new SpellDestroyMessage { SpellInstanceId = SpellInstanceId });
        }
    }
}
