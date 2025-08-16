using UnityEngine;
using UnityEngine.UI;

namespace LunacidCoopMod
{
    public static class PlayerVisuals
    {
        public static void FaceCamera(GameObject dummy)
        {
            if (!dummy) return;
            var cam = Camera.main;
            if (!cam) return;

            var canvas = dummy.transform.Find("NameplateCanvas");
            if (canvas)
            {
                canvas.LookAt(cam.transform);
                canvas.Rotate(0f, 180f, 0f);
            }
        }

        public static GameObject SpawnPlayerDummy(string name, Vector3 position, Color color, string weaponName)
        {
            Plugin.Log.LogInfo($"[PlayerSync] SpawnPlayerDummy: {name} at {position} with weapon {weaponName}");

            GameObject root = new GameObject(name);
            root.transform.position = position;

            GameObject canvasObj = new GameObject("NameplateCanvas");
            canvasObj.transform.SetParent(root.transform);
            canvasObj.transform.localPosition = new Vector3(0, 1.8f, 0);
            canvasObj.transform.localRotation = Quaternion.identity;
            canvasObj.transform.localScale = Vector3.one * 0.01f;

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(200f, 50f);

            GameObject textObj = new GameObject("NameText");
            textObj.transform.SetParent(canvasObj.transform, false);

            Text text = textObj.AddComponent<Text>();
            text.text = name;
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = color;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var textRect = text.GetComponent<RectTransform>();
            textRect.sizeDelta = canvasRect.sizeDelta;
            textRect.localPosition = Vector3.zero;

            var visualHolder = new GameObject("Visual").transform;
            visualHolder.SetParent(root.transform);
            visualHolder.localPosition = Vector3.zero;

            TryAttachWeapon(root, visualHolder, weaponName);

            Plugin.Log.LogInfo($"[PlayerSync] Successfully created dummy: {name}");
            return root;
        }

        public static void UpdateWeaponVisuals(GameObject dummy, string weaponName)
        {
            if (!dummy || string.IsNullOrEmpty(weaponName) || weaponName == "None") return;
            Plugin.Log.LogInfo($"[PlayerSync] UpdateWeaponVisuals: {dummy.name} -> {weaponName}");

            var visualHolder = dummy.transform.Find("Visual");
            if (!visualHolder)
            {
                var visualObj = new GameObject("Visual");
                visualObj.transform.SetParent(dummy.transform);
                visualObj.transform.localPosition = Vector3.zero;
                visualObj.transform.localRotation = Quaternion.identity;
                visualObj.transform.localScale = Vector3.one;
                visualHolder = visualObj.transform;
            }

            foreach (Transform child in visualHolder) Object.Destroy(child.gameObject);

            TryAttachWeapon(dummy, visualHolder, weaponName);
        }

        private static void TryAttachWeapon(GameObject root, Transform visualHolder, string weaponName)
        {
            GameObject prefab = Resources.Load<GameObject>($"WEPS/{weaponName}");
            if (!prefab)
            {
                Plugin.Log.LogWarning($"[PlayerSync] Could not find weapon prefab: WEPS/{weaponName}");
                return;
            }

            Plugin.Log.LogInfo($"[PlayerSync] Loading weapon prefab: WEPS/{weaponName}");
            var instance = Object.Instantiate(prefab, visualHolder);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            instance.SetActive(true);

            var weaponScript = instance.GetComponent<Weapon_scr>();
            if (weaponScript)
            {
                weaponScript.Player = root;
                weaponScript.enabled = true;
                Plugin.Log.LogInfo($"[PlayerSync] Weapon script configured for {weaponName}");
            }
        }
    }
}
