using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    public class MPMenu : MonoBehaviour
    {
        public static MPMenu Instance;

        private bool showMenu = false;
        private string ipInput = "127.0.0.1";
        private string statusText = "";
        private bool spawnedRat = false;
        private GameObject rat;

        private string targetSceneName = "HUB_01";

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!spawnedRat)
            {
                var currentScene = SceneManager.GetActiveScene();
                if (currentScene.name == targetSceneName)
                {
                    SpawnRat();
                    spawnedRat = true;
                }
            }
        }

        private void SpawnRat()
        {
            GameObject prefab = Resources.Load<GameObject>("ded/RAT_DED1");
            if (prefab == null)
            {
                Plugin.Log.LogError("[MPMenu] Could not load prefab: ded/RAT_DED1");
                return;
            }

            Vector3 pos = new Vector3(15.5f, 0.95f, -9.65f);
            Quaternion rot = Quaternion.Euler(75f, 0f, 0f);

            rat = Instantiate(prefab, pos, rot);
            rat.name = "CustomDeadRat";

            // Remove Loot_scr if present
            var loot = rat.GetComponent("Loot_scr") as MonoBehaviour;
            if (loot != null) Destroy(loot);

            // Add interaction
            rat.AddComponent<MPMenuInteraction>();

            // Add a generous trigger collider (like stool setup)
            var trigger = rat.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(2f, 2f, 2f);  // interaction zone
            trigger.center = Vector3.zero;

            Plugin.Log.LogInfo("[MPMenu] Spawned rat and hooked up multiplayer menu interaction");
        }

        public void ToggleMenu()
        {
            showMenu = !showMenu;
            Plugin.Log.LogInfo($"[MPMenu] Menu toggled: {(showMenu ? "ON" : "OFF")}");
        }

        private void OnGUI()
        {
            if (!showMenu) return;

            GUI.Box(new Rect(20, 20, 300, 220), $"Multiplayer Menu v{Plugin.ModVersion}");

            var net = NetworkManager.Instance;
            if (net == null)
            {
                GUI.Label(new Rect(40, 60, 240, 20), "NetworkManager not found!");
                return;
            }

            if (!net.IsConnected && !net.IsHost)
            {
                if (GUI.Button(new Rect(40, 60, 220, 30), "Host Game"))
                {
                    net.StartHost(7777);
                    statusText = "Hosting on port 7777...";
                }

                GUI.Label(new Rect(40, 100, 240, 20), "Join IP:");
                ipInput = GUI.TextField(new Rect(40, 120, 220, 25), ipInput);

                if (GUI.Button(new Rect(40, 150, 220, 30), "Join Game"))
                {
                    net.ConnectToHost(ipInput, 7777);
                    statusText = $"Connecting to {ipInput}...";
                }

                GUI.Label(new Rect(40, 180, 240, 20), statusText);
            }
            else
            {
                string connectedText = net.IsHost ? "You are hosting." :
                    $"Connected to host{(string.IsNullOrEmpty(net.ConnectedPlayerName) ? "" : $" ({net.ConnectedPlayerName})")}";

                GUI.Label(new Rect(40, 80, 240, 40), connectedText);

                if (GUI.Button(new Rect(40, 130, 220, 30), "Disconnect"))
                {
                    net.Disconnect();
                    statusText = "Disconnected";
                }

                var playerSync = PlayerSyncManager.Instance;
                if (playerSync != null)
                {
                    GUI.Label(new Rect(40, 170, 240, 20), $"Send rate: {playerSync.sendRateHz}Hz");
                }
            }
        }
    }

    public class MPMenuInteraction : MonoBehaviour
    {
        private bool playerInRange = false;
        private GameObject player = null;

        private void OnTriggerEnter(Collider other)
        {
            if (other.name == "PLAYER" || other.CompareTag("Player") ||
                other.name.ToLower().Contains("player"))
            {
                playerInRange = true;
                player = other.gameObject;

                var playerController = other.GetComponent<Player_Control_scr>();
                if (playerController != null)
                {
                    playerController.ACT_TRIGGER = this.gameObject;
                    Plugin.Log.LogInfo("[MPMenuInteraction] Set as ACT_TRIGGER on player");
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.name == "PLAYER" || other.CompareTag("Player") ||
                other.name.ToLower().Contains("player"))
            {
                playerInRange = false;
                player = null;

                var playerController = other.GetComponent<Player_Control_scr>();
                if (playerController != null && playerController.ACT_TRIGGER == this.gameObject)
                {
                    playerController.ACT_TRIGGER = null;
                    Plugin.Log.LogInfo("[MPMenuInteraction] Cleared ACT_TRIGGER on player");
                }
            }
        }

        public void ACT()
        {
            if (playerInRange && player != null)
            {
                Plugin.Log.LogInfo("[MPMenuInteraction] ACT() called! Opening multiplayer menu...");
                MPMenu.Instance.ToggleMenu();
            }
        }
    }
}
