using System.Collections.Generic;
using UnityEngine;

namespace LunacidCoopMod
{
    // Registry of every active player transform (local PLAYER + remote dummies).
    // Replaces hard-coded GameObject.Find("PLAYER") lookups so AI / XP / companions work with multiple players.
    public static class PlayerRegistry
    {
        private static readonly List<Transform> _players = new List<Transform>();

        public static int Count => _players.Count;

        public static void Register(Transform t)
        {
            if (t != null && !_players.Contains(t))
            {
                _players.Add(t);
                CoopLog.PlayerRegistry($"Registered: {t.name}");
            }
        }

        public static void Unregister(Transform t)
        {
            if (_players.Remove(t))
                CoopLog.PlayerRegistry($"Unregistered: {t?.name}");
        }

        // Remove all registrations (call on scene load before re-registering).
        public static void Clear()
        {
            _players.Clear();
            CoopLog.PlayerRegistry("Cleared all registrations");
        }

        // Nearest registered transform (local player or dummy). Null if registry empty.
        public static Transform GetNearest(Vector3 pos)
        {
            Transform best = null;
            float bestSqr = float.MaxValue;
            foreach (var t in _players)
            {
                if (t == null) continue;
                float d = (pos - t.position).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = t; }
            }
            return best;
        }

        // Nearest "real" player (has Player_Control_scr); excludes dummies. Use for XP/stats/health lookups.
        public static Transform GetNearestReal(Vector3 pos)
        {
            Transform best = null;
            float bestSqr = float.MaxValue;
            foreach (var t in _players)
            {
                if (t == null) continue;
                if (t.GetComponent<Player_Control_scr>() == null) continue;
                float d = (pos - t.position).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = t; }
            }
            return best;
        }
    }
}
