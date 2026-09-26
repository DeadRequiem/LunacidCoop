using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Sibling-index paths for naming GameObjects deterministically across machines.
    // Format: "<rootIdx>/<childIdx>/.../<leafIdx>".
    public static class ScenePath
    {
        // Builds a path from the scene root down to t.
        public static string BuildScenePath(Transform t)
        {
            if (t == null) return "";
            var indices = new List<int>();
            while (t.parent != null)
            {
                indices.Insert(0, t.GetSiblingIndex());
                t = t.parent;
            }
            // t is now a scene root. Find its index among the scene's root GameObjects.
            var roots = t.gameObject.scene.GetRootGameObjects();
            int rootIdx = Array.IndexOf(roots, t.gameObject);
            indices.Insert(0, rootIdx);
            return string.Join("/", indices.ConvertAll(i => i.ToString()).ToArray());
        }

        // Path from root down to t. Returns "" if t == root.
        public static string BuildRelativePath(Transform t, Transform root)
        {
            if (t == root || t == null) return "";
            var indices = new List<int>();
            while (t != null && t != root)
            {
                indices.Insert(0, t.GetSiblingIndex());
                t = t.parent;
            }
            return string.Join("/", indices.ConvertAll(i => i.ToString()).ToArray());
        }

        // Inverse of BuildRelativePath. Empty path returns root; null if any index is out of bounds.
        public static Transform ResolveRelativePath(string path, Transform root)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;

            Transform t = root;
            foreach (var part in path.Split('/'))
            {
                if (!int.TryParse(part, out int idx)) return null;
                if (idx < 0 || idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            return t;
        }

        // Resolves a sibling-index path back to its GameObject. Returns null if any index is out of bounds.
        public static GameObject ResolveScenePath(string path, Scene scene)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            var roots = scene.GetRootGameObjects();
            if (!int.TryParse(parts[0], out int rootIdx)) return null;
            if (rootIdx < 0 || rootIdx >= roots.Length) return null;
            Transform t = roots[rootIdx].transform;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int idx)) return null;
                if (idx < 0 || idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            return t.gameObject;
        }
    }
}
