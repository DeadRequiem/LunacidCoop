using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LunacidCoopMod
{
    [BepInPlugin("com.stilIborn.lunacidcoop", "Lunacid Online Co-Op", "4.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModVersion = "4.0.0-json";
        public static ManualLogSource Log;
        public static Plugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Patch Harmony classes
            var harmony = new Harmony("com.stilIborn.lunacidcoop");
            harmony.PatchAll(typeof(Plugin).Assembly);

            // Ensure network manager exists
            if (NetworkManager.Instance == null)
            {
                var netGO = new GameObject("NetworkManager");
                netGO.AddComponent<NetworkManager>();
                DontDestroyOnLoad(netGO);
                Log.LogInfo("[Co-Op] NetworkManager created");
            }

            // Ensure player sync manager exists
            if (PlayerSyncManager.Instance == null)
            {
                var syncGO = new GameObject("PlayerSyncManager");
                syncGO.AddComponent<PlayerSyncManager>();
                DontDestroyOnLoad(syncGO);
                Log.LogInfo("[Co-Op] PlayerSyncManager created");
            }

            // Ensure MPMenu exists (this will now also handle the rat)
            if (MPMenu.Instance == null)
            {
                var menuGO = new GameObject("MPMenu");
                menuGO.AddComponent<MPMenu>();
                DontDestroyOnLoad(menuGO);
                Log.LogInfo("[Co-Op] MPMenu created");
            }

            Log.LogInfo($"[Co-Op] Plugin loaded v{ModVersion}.");
        }

        // Patch to prevent game pausing when connected
        [HarmonyPatch(typeof(Time), nameof(Time.timeScale), MethodType.Setter)]
        public static class Patch_TimeScaleSetter
        {
            static void Prefix(ref float value)
            {
                if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected)
                {
                    value = 1f;
                }
            }
        }
    }
}
