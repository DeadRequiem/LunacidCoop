using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Per-frame player state broadcast + the dictionary of remote-player dummies keyed by PlayerId.
    // Other sync responsibilities live in WeaponSync, SpellSync, WorldStateSync, WorldDestroySync, RigidbodySync, AIPatches.
    public class PlayerSyncManager : MonoBehaviour
    {
        // Lerp factor per second for smoothing dummy position/rotation. Tuned for the 20Hz default send rate.
        private const float DUMMY_LERP_SPEED = 15f;

        // Compensate for the dummy's forward axis being 90 degrees off from Camera.main.forward.
        private static readonly Quaternion ROTATION_OFFSET = Quaternion.Euler(0f, 90f, 0f);
        private static Quaternion ApplyRotationOffset(Quaternion fromWire) => fromWire * ROTATION_OFFSET;

        // Strip the trailing XP/state digits from equipped weapon names ("ELFEN SWORD36" -> "ELFEN SWORD") so the receiver can resolve the prefab.
        private static readonly System.Text.RegularExpressions.Regex TrailingDigits =
            new System.Text.RegularExpressions.Regex(@"\d+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Per-slot dummy color. Index = slot number; both ends derive identical colors without syncing.
        private static readonly Color[] SLOT_COLORS = {
            new Color(0.4f, 0.6f, 1.0f), // slot 0 host: blue
            new Color(1.0f, 0.4f, 0.4f), // slot 1: red
            new Color(0.4f, 1.0f, 0.4f), // slot 2: green
            new Color(1.0f, 0.9f, 0.3f), // slot 3: yellow
        };

        public static PlayerSyncManager Instance;

        // Per-peer dummy state bundle.
        private class PeerDummy
        {
            public GameObject GameObject;
            public Vector3 TargetPos;
            public Quaternion TargetRot;
            public string LastWeapon = "None";
            // HealthMax 0 means we have not had an update carrying health yet.
            public int Health;
            public int HealthMax;
            public string LastKnownScene = "";
            // -1 until lobby state arrives. If the dummy was created before that, OnPeerJoined retints with the real slot color.
            public int AppliedSlot = -1;
        }

        private readonly Dictionary<string, PeerDummy> dummies = new Dictionary<string, PeerDummy>();

        // Fires once when a peer's dummy is freshly created (they just appeared in our scene).
        // NpcScanner uses this to re-request scene state when host arrives after the initial DelayedScan.
        public event System.Action<string> OnPeerEnteredOurScene;

        private float lastSend;

        // Cached by RegisterLocalPlayerWhenReady. GameObject.Find walks every active object and
        // string-compares each one; it was running every rendered frame while connected.
        private GameObject localPlayer;

        // Local player identifier, sourced from transport so game-layer messages match the host's slot table.
        public string LocalPlayerId => SteamNetworkManager.Instance?.LocalPlayerId ?? "";

        // Specific peer's dummy by PlayerId. Null if unknown peer or dummy not yet created.
        public GameObject GetDummy(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            return dummies.TryGetValue(playerId, out var entry) ? entry.GameObject : null;
        }

        // Peer's health as 0..1, or -1 when unknown (no update received yet). PeerPanel's condition dot.
        public float GetHealthFraction(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return -1f;
            if (!dummies.TryGetValue(playerId, out var entry)) return -1f;
            if (entry.HealthMax <= 0) return -1f;
            return Mathf.Clamp01((float)entry.Health / entry.HealthMax);
        }

        // Peer's last-known scene (per their last PlayerUpdateMessage). Empty if no update received yet.
        public string GetLastKnownScene(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "";
            return dummies.TryGetValue(playerId, out var entry) ? (entry.LastKnownScene ?? "") : "";
        }

        // Lowest-slot peer dummy that isn't us. For single-target UI.
        public GameObject GetPrimaryPeerDummy()
        {
            var net = SteamNetworkManager.Instance;
            if (net == null) return null;
            string myId = LocalPlayerId;
            foreach (var entry in net.GetLobbyView())
            {
                if (entry.PlayerId != myId)
                    return GetDummy(entry.PlayerId);
            }
            return null;
        }

        // Host's dummy as seen by us (slot 0). Null if we are the host or the host isn't in our scene.
        public GameObject HostDummy
        {
            get
            {
                var net = SteamNetworkManager.Instance;
                if (net == null) return null;
                foreach (var entry in net.GetLobbyView())
                {
                    if (entry.Slot != SlotTable.HOST_SLOT) continue;
                    if (entry.PlayerId == LocalPlayerId) return null; // we are the host
                    return GetDummy(entry.PlayerId);
                }
                return null;
            }
        }

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            SceneManager.sceneLoaded += OnSceneLoaded;
            SyncHandler.Subscribe<PlayerUpdateMessage>(OnPlayerUpdate);

            var net = SteamNetworkManager.Instance;
            if (net != null)
            {
                net.OnPeerJoined   += OnPeerJoined;
                net.OnPeerLeft     += OnPeerLeft;
                net.OnDisconnected += OnNetDisconnected;
            }

            Plugin.Log.LogInfo("[PlayerSync] Initialized");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            var net = SteamNetworkManager.Instance;
            if (net != null)
            {
                net.OnPeerJoined   -= OnPeerJoined;
                net.OnPeerLeft     -= OnPeerLeft;
                net.OnDisconnected -= OnNetDisconnected;
            }
        }

        // Fires per peer roster-add. Rescales NPCs and retints any dummy that was created with a fallback color before lobby state arrived.
        // Dummy creation itself is lazy (first PlayerUpdateMessage), not here.
        private void OnPeerJoined(ClientSlot slot)
        {
            Plugin.Log.LogInfo($"[PlayerSync] Peer joined slot {slot.Slot} ({slot.PlayerName}) - rescaling NPCs");
            AIPatches.HpScaling.RescaleAll();

            if (!string.IsNullOrEmpty(slot.PlayerId) &&
                dummies.TryGetValue(slot.PlayerId, out var entry) &&
                entry.GameObject != null &&
                entry.AppliedSlot != slot.Slot)
            {
                PlayerVisuals.SetNameplateColor(entry.GameObject, SlotColor(slot.Slot));
                entry.AppliedSlot = slot.Slot;
                Plugin.Log.LogInfo($"[PlayerSync] Re-tinted dummy for '{slot.PlayerName}' to slot-{slot.Slot} color");
            }
        }

        // Fires per peer roster-remove (single peer leave; session stays alive).
        private void OnPeerLeft(ClientSlot slot)
        {
            Plugin.Log.LogInfo($"[PlayerSync] Peer left slot {slot.Slot} ({slot.PlayerName})");
            DestroyDummy(slot.PlayerId);
            AIPatches.HpScaling.RescaleAll();
        }

        // Full session teardown - clears any remaining dummies as a safety net.
        private void OnNetDisconnected()
        {
            Plugin.Log.LogInfo("[PlayerSync] Peer disconnected - clearing remaining dummies and state");
            ClearAllDummies();
            AIPatches.HpScaling.RescaleAll();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"[PlayerSync] Scene loaded: {scene.name} - cleaning up dummies");

            // Loading into the main menu = user left the game/save. Drop any active session.
            if (scene.name == "MainMenu")
            {
                var net = SteamNetworkManager.Instance;
                if (net != null && (net.IsConnected || net.IsHost || net.IsClient))
                {
                    Plugin.Log.LogInfo("[PlayerSync] MainMenu loaded - disconnecting from session");
                    net.Disconnect();
                }
            }

            localPlayer = null;
            PlayerRegistry.Clear();
            AIPatches.HpScaling.ClearAll();
            ClearAllDummies();

            StartCoroutine(RegisterLocalPlayerWhenReady());
        }

        private IEnumerator RegisterLocalPlayerWhenReady()
        {
            // Poll rather than spin: scenes with no PLAYER (the title screen) never satisfy this, and
            // a per-frame GameObject.Find there runs for as long as the player sits on the menu.
            GameObject player = null;
            while (player == null)
            {
                yield return new WaitForSeconds(0.25f);
                player = GameObject.Find("PLAYER");
            }
            localPlayer = player;
            PlayerRegistry.Register(player.transform);
            Plugin.Log.LogInfo($"[PlayerSync] Registered local PLAYER in scene {SceneManager.GetActiveScene().name}");
        }

        private void Update()
        {
            if (SyncHandler.IsConnected)
            {
                float rate = Plugin.PlayerSendRateHz != null ? Plugin.PlayerSendRateHz.Value : 20f;
                if (Time.time - lastSend >= (1f / Mathf.Max(1f, rate)))
                {
                    var me = localPlayer != null ? localPlayer : FindLocalPlayer();
                    if (me != null)
                    {
                        SendPlayerUpdate(me);
                        lastSend = Time.time;
                    }
                }
            }

            // Iterate-and-interpolate every dummy. Dict mutation only happens on receive/event callbacks, not Update, so safe.
            foreach (var kvp in dummies)
            {
                var entry = kvp.Value;
                InterpolateDummy(entry.GameObject, entry.TargetPos, entry.TargetRot);
                PlayerVisuals.FaceCamera(entry.GameObject);
            }
        }

        private static void InterpolateDummy(GameObject dummy, Vector3 targetPos, Quaternion targetRot)
        {
            if (dummy == null) return;
            float t = DUMMY_LERP_SPEED * Time.deltaTime;
            dummy.transform.position = Vector3.Lerp(dummy.transform.position, targetPos, t);
            dummy.transform.rotation = Quaternion.Slerp(dummy.transform.rotation, targetRot, t);
        }

        private void SendPlayerUpdate(GameObject me)
        {
            var control = me.GetComponent<Player_Control_scr>();
            // Ceil, not round: a player on 0.4 HP is alive, and PeerPanel reads 0 as dead.
            int health = (control?.CON?.CURRENT_PL_DATA != null)
                ? Mathf.CeilToInt(control.CON.CURRENT_PL_DATA.PLAYER_H)
                : 100;
            int healthMax = control?.CON != null ? Mathf.RoundToInt(control.CON.PLAYER_MAX_HP) : 0;

            // Camera rotation (not player rotation) as a quaternion - avoids euler-decomposition ambiguity on the receiver.
            var cam = Camera.main;
            Quaternion rot = cam != null ? cam.transform.rotation : me.transform.rotation;

            SyncHandler.Send(new PlayerUpdateMessage
            {
                PlayerId     = LocalPlayerId,
                PlayerName   = PlayerNaming.GetLocalPlayerName(SyncHandler.IsHost),
                Position     = me.transform.position,
                Rotation     = rot,
                WeaponName   = GetCurrentWeaponName(me),
                Health       = health,
                HealthMax    = healthMax,
                CurrentScene = SceneManager.GetActiveScene().name
            });
        }

        private void OnPlayerUpdate(PlayerUpdateMessage u)
        {
            // Drop our own echo. PlayerUpdateMessage.PlayerId matches LocalPlayerId for self-updates.
            if (string.IsNullOrEmpty(u.PlayerId)) return;
            if (u.PlayerId == LocalPlayerId) return;

            string peerId = u.PlayerId;

            // Get-or-create the entry. PeerDummy exists before its GameObject does so LastKnownScene survives scene-mismatch teardowns.
            if (!dummies.TryGetValue(peerId, out var entry))
            {
                entry = new PeerDummy();
                dummies[peerId] = entry;
            }

            // Kept even when they are in another scene, so the panel can still show their condition.
            entry.Health    = u.Health;
            entry.HealthMax = u.HealthMax;

            // Track their most recently-reported scene for PeerPanel's "(in SCENE)" display.
            if (!string.IsNullOrEmpty(u.CurrentScene))
                entry.LastKnownScene = u.CurrentScene;

            var myScene = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(u.CurrentScene) && u.CurrentScene != myScene)
            {
                // Different scene: tear down the dummy but keep the entry alive (preserves LastKnownScene).
                if (entry.GameObject != null)
                {
                    Plugin.Log.LogInfo($"[PlayerSync] Peer '{u.PlayerName}' is in '{u.CurrentScene}', we're in '{myScene}' - removing their dummy");
                    PlayerRegistry.Unregister(entry.GameObject.transform);
                    AIPatches.RetargetAwayFrom(entry.GameObject.transform);
                    Destroy(entry.GameObject);
                    entry.GameObject = null;
                    entry.LastWeapon = "None";
                }
                return;
            }

            string weapon = string.IsNullOrEmpty(u.WeaponName) ? "None" : u.WeaponName;
            string displayName = string.IsNullOrEmpty(u.PlayerName) ? "Peer" : u.PlayerName;

            Quaternion targetRot = ApplyRotationOffset(u.Rotation);

            // First-sight dummy creation. If slot is unknown (no lobby state yet) we use fallback color and OnPeerJoined retints later.
            if (entry.GameObject == null)
            {
                int slot = GetSlotForPlayerId(peerId);
                Color color = SlotColor(slot);

                Plugin.Log.LogInfo($"[PlayerSync] Creating dummy for '{displayName}' (PlayerId={peerId}, slot={slot})");
                entry.GameObject = PlayerVisuals.CreateDummy(displayName, color);
                PlayerVisuals.SetWeapon(entry.GameObject, weapon);
                entry.LastWeapon = weapon;
                entry.AppliedSlot = slot;
                PlayerRegistry.Register(entry.GameObject.transform);
                entry.GameObject.transform.position = u.Position;
                entry.GameObject.transform.rotation = targetRot;

                // Fire the "peer appeared in our scene" event. Subscribers can react to a
                // peer arriving (the host coming to our scene, in particular - NpcScanner
                // uses this to request fresh scene state when host arrives late).
                OnPeerEnteredOurScene?.Invoke(peerId);
            }

            entry.TargetPos = u.Position;
            entry.TargetRot = targetRot;

            if (entry.LastWeapon != weapon)
            {
                entry.LastWeapon = weapon;
                PlayerVisuals.SetWeapon(entry.GameObject, weapon);
            }
        }

        private int GetSlotForPlayerId(string playerId)
        {
            var net = SteamNetworkManager.Instance;
            if (net == null) return -1;
            foreach (var entry in net.GetLobbyView())
            {
                if (entry.PlayerId == playerId) return entry.Slot;
            }
            return -1;
        }

        // Public for HUD elements to reuse the same palette.
        public static Color SlotColor(int slot)
        {
            if (slot < 0 || slot >= SLOT_COLORS.Length) return Color.white;
            return SLOT_COLORS[slot];
        }

        private void DestroyDummy(string playerId)
        {
            if (!dummies.TryGetValue(playerId, out var entry)) return;
            if (entry.GameObject != null)
            {
                PlayerRegistry.Unregister(entry.GameObject.transform);
                AIPatches.RetargetAwayFrom(entry.GameObject.transform);
                Destroy(entry.GameObject);
            }
            dummies.Remove(playerId);
        }

        private void ClearAllDummies()
        {
            foreach (var kvp in dummies)
            {
                var entry = kvp.Value;
                if (entry.GameObject != null)
                {
                    PlayerRegistry.Unregister(entry.GameObject.transform);
                    AIPatches.RetargetAwayFrom(entry.GameObject.transform);
                    Destroy(entry.GameObject);
                }
            }
            dummies.Clear();
        }

        private GameObject FindLocalPlayer() => GameObject.Find("PLAYER");

        private string GetCurrentWeaponName(GameObject player)
        {
            var control = player?.GetComponent<Player_Control_scr>();
            if (control == null || control.CON == null) return "None";

            var data = control.CON.CURRENT_PL_DATA;
            int slot = control.CON.EQ_SLOT;

            // Explicit bounds-check: only slots 0 and 1 map to weapons.
            string weapon;
            if (slot == 0)      weapon = data.WEP1;
            else if (slot == 1) weapon = data.WEP2;
            else                weapon = "";

            if (string.IsNullOrEmpty(weapon)) return "None";

            // Strip the trailing XP/state digits.
            string clean = TrailingDigits.Replace(weapon, "").TrimEnd();
            return string.IsNullOrEmpty(clean) ? "None" : clean.ToUpperInvariant();
        }
    }
}
