using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    public class PlayerSyncManager : MonoBehaviour
    {
        public static PlayerSyncManager Instance;

        [Header("Send/Recv")]
        public float sendRateHz = 15f;

        private string localPlayerId;
        private float lastSend;
        private NetworkManager net;

        private static readonly string[] WeaponPrefabs = new string[]
        {
            "AXE OF HARMING","BATTLE AXE","BLADE OF JUSZTINA","BLADE OF OPHELIA","BLESSED WIND",
            "BRITTLE ARMING SWORD","BROKEN HILT","BROKEN LANCE","CAVALRY SABER","CORRUPTED DAGGER",
            "CROSSBOW","CURSED BLADE","CURSED GREATSWORD","DARK GREATSWORD","DARK RAPIER",
            "DEATH SCYTHE","DEATH","DOUBLE CROSSBOW","ELFEN BOW","ELFEN LONGSWORD",
            "ELFEN SWORD","FIRE SWORD","FISHING SPEAR","FLAIL","FROZEN HAMMER",
            "GHOST SWORD","GOLDEN KHOPESH","GOLDEN SICKLE","HALBERD","HAMMER OF CRUELTY",
            "HERITAGE SWORD","ICE SICKLE","IRON CLAW","IRON CLUB","IRON TORCH",
            "JAILORS CANDLE","JOTUNN SLAYER","KING'S SCABBARD","LIMBO","LUCID BLADE",
            "LYRIAN GREATSWORD","LYRIAN LONGSWORD","MARAUDER BLACK FLAIL","MOONLIGHT","OBSIDIAN CURSEBRAND",
            "OBSIDIAN POISONGUARD","OBSIDIAN SEAL","PICKAXE","POISON CLAW","PRIVATEER MUSKET",
            "RAPIER","REPLICA SWORD","RITUAL DAGGER","RUSTED SWORD","SAINT ISHII",
            "SERPENT FANG","SHADOW BLADE","SHINING BLADE","SILVER RAPIER","SKELETON AXE",
            "STEEL CLAW","STEEL CLUB","STEEL LANCE","STEEL NEEDLE","STEEL SPEAR",
            "STONE CLUB","SUCSARIAN DAGGER","SUCSARIAN SPEAR","THORN","TORCH",
            "TWISTED STAFF","VAMPIRE HUNTER SWORD","WAND OF POWER","WOLFRAM GREATSWORD","WOODEN SHIELD"
        };

        private static int GetWeaponID(string name) => Array.IndexOf(WeaponPrefabs, name);
        private static string GetWeaponNameByID(int id) => (id < 0 || id >= WeaponPrefabs.Length) ? "None" : WeaponPrefabs[id];

        private GameObject hostDummy;
        private GameObject clientDummy;
        private string lastHostWeapon = "None";
        private string lastClientWeapon = "None";

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            localPlayerId = Guid.NewGuid().ToString();

            SceneManager.sceneLoaded += OnSceneLoaded;
            Plugin.Log.LogInfo("[PlayerSync] PlayerSyncManager initialized");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"[PlayerSync] Scene loaded: {scene.name} - cleaning up dummies");

            if (hostDummy != null) Destroy(hostDummy);
            if (clientDummy != null) Destroy(clientDummy);
            hostDummy = null;
            clientDummy = null;

            lastHostWeapon = "None";
            lastClientWeapon = "None";
        }

        private void OnEnable()
        {
            net = NetworkManager.Instance;
            if (net != null) net.OnMessageReceived += HandleNetMessage;
        }

        private void OnDisable()
        {
            if (net != null) net.OnMessageReceived -= HandleNetMessage;
        }

        private void Update()
        {
            if (net != null && net.IsConnected && Time.time - lastSend >= (1f / Mathf.Max(1f, sendRateHz)))
            {
                var me = FindLocalPlayer();
                if (me != null)
                {
                    SendPlayerUpdate(me);
                    lastSend = Time.time;
                }
            }

            PlayerVisuals.FaceCamera(hostDummy);
            PlayerVisuals.FaceCamera(clientDummy);
        }

        private void SendPlayerUpdate(GameObject me)
        {
            var msg = new PlayerUpdateMessage
            {
                PlayerId = localPlayerId,
                Position = me.transform.position,
                Rotation = me.transform.eulerAngles,
                WeaponName = GetCurrentWeaponName(me),
                Health = 100,
                CurrentScene = SceneManager.GetActiveScene().name
            };
            net.SendMessage(msg);

            // 🔹 quieted (was Info)
            Plugin.Log.LogDebug($"[PlayerSync] Sent update: {msg.WeaponName} at {msg.Position}");
        }

        private void HandleNetMessage(NetworkMessage m)
        {
            if (m is PlayerUpdateMessage u) OnPlayerUpdate(u);
        }

        private void OnPlayerUpdate(PlayerUpdateMessage u)
        {
            if (!string.IsNullOrEmpty(u.PlayerId) && u.PlayerId == localPlayerId) return;

            var myScene = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(u.CurrentScene) && u.CurrentScene != myScene)
                return;

            if (string.IsNullOrEmpty(u.WeaponName) || u.WeaponName == "None")
                return;

            bool weAreHost = (net != null && net.IsHost);

            if (weAreHost)
            {
                Plugin.Log.LogDebug($"[PlayerSync] Host update: {u.WeaponName} at {u.Position}");

                if (clientDummy == null)
                {
                    Plugin.Log.LogInfo("[PlayerSync] Creating client dummy");
                    clientDummy = PlayerVisuals.SpawnPlayerDummy("Client", u.Position, Color.red, u.WeaponName);
                }
                else
                {
                    clientDummy.transform.position = u.Position;
                    clientDummy.transform.rotation = Quaternion.Euler(u.Rotation);

                    if (lastClientWeapon != u.WeaponName)
                    {
                        Plugin.Log.LogDebug($"[PlayerSync] Client weapon changed: {lastClientWeapon} -> {u.WeaponName}");
                        lastClientWeapon = u.WeaponName;
                        PlayerVisuals.UpdateWeaponVisuals(clientDummy, lastClientWeapon);
                    }
                }
            }
            else
            {
                Plugin.Log.LogDebug($"[PlayerSync] Client update: {u.WeaponName} at {u.Position}");

                if (hostDummy == null)
                {
                    Plugin.Log.LogInfo("[PlayerSync] Creating host dummy");
                    hostDummy = PlayerVisuals.SpawnPlayerDummy("Host", u.Position, Color.blue, u.WeaponName);
                }
                else
                {
                    hostDummy.transform.position = u.Position;
                    hostDummy.transform.rotation = Quaternion.Euler(u.Rotation);

                    if (lastHostWeapon != u.WeaponName)
                    {
                        Plugin.Log.LogDebug($"[PlayerSync] Host weapon changed: {lastHostWeapon} -> {u.WeaponName}");
                        lastHostWeapon = u.WeaponName;
                        PlayerVisuals.UpdateWeaponVisuals(hostDummy, lastHostWeapon);
                    }
                }
            }
        }

        private GameObject FindLocalPlayer()
        {
            return GameObject.Find("PLAYER");
        }

        private string GetCurrentWeaponName(GameObject player)
        {
            var control = player?.GetComponent<Player_Control_scr>();
            if (control == null || control.CON == null) return "None";

            var data = control.CON.CURRENT_PL_DATA;
            int slot = control.CON.EQ_SLOT;
            string weapon = (slot == 0) ? data.WEP1 : data.WEP2;

            if (string.IsNullOrEmpty(weapon)) return "None";

            int index = GetWeaponID(weapon.ToUpperInvariant());
            return (index >= 0) ? WeaponPrefabs[index] : "None";
        }
    }
}
