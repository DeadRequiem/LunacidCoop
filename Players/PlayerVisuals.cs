using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace LunacidCoopMod
{
    // Dummy GameObject + world-space nameplate + weapon model.
    public static class PlayerVisuals
    {
        private const int NAMEPLATE_MAX_CHARS = 32;
        private const float NAMEPLATE_HEIGHT = 1.8f;

        public static void FaceCamera(GameObject dummy)
        {
            if (!dummy) return;
            var cam = Camera.main;
            if (!cam) return;

            var canvas = dummy.transform.Find("Nameplate");
            if (canvas)
            {
                // World-space pin: dummy rotates with the camera, so localPosition would swing under pitch.
                canvas.position = dummy.transform.position + Vector3.up * NAMEPLATE_HEIGHT;
                canvas.LookAt(cam.transform);
                canvas.Rotate(0f, 180f, 0f);
            }
        }

        // Creates dummy with nameplate and empty visual holder. Caller sets position then calls SetWeapon
        public static GameObject CreateDummy(string label, Color color)
        {
            // Prefixed so a peer whose Steam persona is "PLAYER" cannot be returned by the game's
            // (and our own) GameObject.Find("PLAYER"). The nameplate still shows the raw label.
            var root = new GameObject("CoopPeer_" + label);

            BuildNameplate(root, label, color);

            var vh = new GameObject("Visual");
            vh.transform.SetParent(root.transform);
            vh.transform.localPosition = Vector3.zero;
            vh.transform.localRotation = Quaternion.identity;
            vh.transform.localScale = Vector3.one;

            CoopLog.PlayerVisuals($"Created dummy: {label}");
            return root;
        }

        // Re-tints an existing dummy's nameplate, used when slot info arrives after dummy creation.
        public static void SetNameplateColor(GameObject dummy, Color color)
        {
            if (!dummy) return;
            var canvas = dummy.transform.Find("Nameplate");
            if (canvas == null) return;
            var label = canvas.Find("Label");
            if (label == null) return;
            var text = label.GetComponent<Text>();
            if (text != null) text.color = color;
        }

        // Swaps the weapon model on an existing dummy.
        public static void SetWeapon(GameObject dummy, string weaponName)
        {
            if (!dummy) return;

            var visualHolder = dummy.transform.Find("Visual");
            if (visualHolder == null)
            {
                var vh = new GameObject("Visual");
                vh.transform.SetParent(dummy.transform);
                vh.transform.localPosition = Vector3.zero;
                vh.transform.localRotation = Quaternion.identity;
                vh.transform.localScale = Vector3.one;
                visualHolder = vh.transform;
            }

            foreach (Transform child in visualHolder)
                Object.Destroy(child.gameObject);

            if (string.IsNullOrEmpty(weaponName) || weaponName == "None") return;

            var prefab = Resources.Load<GameObject>($"WEPS/{weaponName}");
            if (!prefab)
            {
                Plugin.Log.LogWarning($"[PlayerVisuals] Missing prefab: WEPS/{weaponName}");
                return;
            }

            // Deactivated so Weapon_scr.OnEnable does not run during Instantiate.
            bool holderWasActive = visualHolder.gameObject.activeSelf;
            visualHolder.gameObject.SetActive(false);

            var instance = Object.Instantiate(prefab, visualHolder);
            instance.AddComponent<CoopDummyWeapon>();
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            instance.SetActive(true);

            // Point Weapon_scr at the dummy so its Update doesn't touch the local player's state.
            var ws = instance.GetComponent<Weapon_scr>();
            if (ws != null)
            {
                ws.Player = dummy;

                // Manually wire Hand_Anim since OnEnable NRE'd before AddClip ran.
                var handAnim = instance.AddComponent<Animation>();

                if (ws.Ready_anim != null) { handAnim.AddClip(ws.Ready_anim, ws.Ready_anim.name); handAnim.clip = ws.Ready_anim; }
                if (ws.Idle != null)       { handAnim.AddClip(ws.Idle, ws.Idle.name); }
                if (ws.Attack_Anims != null)
                    foreach (var clip in ws.Attack_Anims)
                        if (clip != null) handAnim.AddClip(clip, clip.name);
                if (ws.Block_Anims != null)
                    foreach (var clip in ws.Block_Anims)
                        if (clip != null) handAnim.AddClip(clip, clip.name);

                ws.Hand_Anim = handAnim;

                // Disable ranged HUD / cursed-damage specials whose Update would NRE against the dummy root.
                bool safeToRun = ws.type == 0 && ws.special != 13 && ws.special != 18;
                ws.enabled = safeToRun;

                // Manual initial idle; the game's Reset() never runs on a dummy weapon.
                if (ws.Idle != null)           handAnim.CrossFade(ws.Idle.name, 0f);
                else if (ws.Ready_anim != null) handAnim.Play(ws.Ready_anim.name);
            }

            visualHolder.gameObject.SetActive(holderWasActive);
            CoopLog.PlayerVisuals($"Set weapon: {weaponName} on {dummy.name}");
        }

        // Marks a weapon that belongs to a peer dummy rather than a real player.
        internal class CoopDummyWeapon : MonoBehaviour { }

        private static void BuildNameplate(GameObject root, string label, Color color)
        {
            var np = new GameObject("Nameplate");
            np.transform.SetParent(root.transform);
            np.transform.localPosition = new Vector3(0f, NAMEPLATE_HEIGHT, 0f);
            np.transform.localRotation = Quaternion.identity;
            np.transform.localScale = Vector3.one * 0.01f;

            var canvas = np.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = canvas.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(700f, 50f);

            // Hard cap on displayed length, truncated after 32 chars.
            string display = (label != null && label.Length > NAMEPLATE_MAX_CHARS)
                ? label.Substring(0, NAMEPLATE_MAX_CHARS)
                : (label ?? "");

            var textGO = new GameObject("Label");
            textGO.transform.SetParent(np.transform, false);
            var text = textGO.AddComponent<Text>();
            text.text = display;
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = color;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            var trt = text.GetComponent<RectTransform>();
            trt.sizeDelta = rt.sizeDelta;
            trt.localPosition = Vector3.zero;
        }
    }

    // OnEnable resolves Player via GameObject.Find, so on a dummy it would hit the real local player.
    [HarmonyPatch(typeof(Weapon_scr), "OnEnable")]
    internal static class Patch_Weapon_OnEnable_Dummy
    {
        static bool Prefix(Weapon_scr __instance)
        {
            return __instance.GetComponent<PlayerVisuals.CoopDummyWeapon>() == null;
        }
    }
}
