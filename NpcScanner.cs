using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;

namespace LunacidCoopMod
{
    public class NpcScanner : MonoBehaviour
    {
        public static NpcScanner Instance;

        private Dictionary<int, AI_simple> idToNpc = new Dictionary<int, AI_simple>();
        private Dictionary<AI_simple, int> npcToId = new Dictionary<AI_simple, int>();

        // Health throttling
        private Dictionary<int, float> lastUpdateTime = new Dictionary<int, float>();
        private Dictionary<int, float> lastHealthValue = new Dictionary<int, float>();
        private const float MIN_UPDATE_INTERVAL = 0.1f;

        // Position interpolation for smooth movement
        private Dictionary<int, Vector3> targetPositions = new Dictionary<int, Vector3>();
        private Dictionary<int, Vector3> targetRotations = new Dictionary<int, Vector3>();
        private Dictionary<int, float> positionLerpSpeed = new Dictionary<int, float>();

        // Position throttling (less aggressive)
        private Dictionary<int, Vector3> lastSentPositions = new Dictionary<int, Vector3>();
        private Dictionary<int, float> lastPositionSend = new Dictionary<int, float>();
        private const float POSITION_SEND_INTERVAL = 0.5f; // Send less frequently
        private const float MIN_MOVEMENT_DISTANCE = 1.0f; // Larger threshold

        // State sync for new clients
        private Dictionary<int, NpcState> npcStates = new Dictionary<int, NpcState>();

        private NetworkManager net;
        private bool sceneReady = false;

        private struct NpcState
        {
            public Vector3 position;
            public Vector3 rotation;
            public float health;
            public bool isDead;
        }

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            SceneManager.sceneLoaded += OnSceneLoaded;
            Plugin.Log.LogInfo("[NpcScanner] Initialized");
        }

        private void OnEnable()
        {
            net = NetworkManager.Instance;
            if (net != null)
            {
                net.OnMessageReceived += HandleMessage;
                net.OnConnected += OnClientConnected;
                Plugin.Log.LogInfo("[NpcScanner] Subscribed to network messages");
            }
        }

        private void OnDisable()
        {
            if (net != null)
            {
                net.OnMessageReceived -= HandleMessage;
                net.OnConnected -= OnClientConnected;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Update()
        {
            if (!sceneReady) return;

            // Interpolate NPC positions for smooth movement (client side)
            if (net?.IsClient == true)
            {
                InterpolateNpcPositions();
            }

            // Send periodic position updates (host only, less frequent)
            if (net?.IsHost == true && Time.time % POSITION_SEND_INTERVAL < Time.deltaTime)
            {
                SendPositionUpdates();
            }
        }

        private void InterpolateNpcPositions()
        {
            foreach (var kvp in idToNpc)
            {
                var npc = kvp.Value;
                var npcId = kvp.Key;

                if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                if (npc.health <= 0) continue; // Don't move dead NPCs

                if (targetPositions.TryGetValue(npcId, out Vector3 targetPos))
                {
                    float lerpSpeed = positionLerpSpeed.TryGetValue(npcId, out float speed) ? speed : 2f;
                    Vector3 currentPos = npc.transform.position;

                    // Only interpolate if we're not already at target
                    float distance = Vector3.Distance(currentPos, targetPos);
                    if (distance > 0.1f)
                    {
                        Vector3 newPos = Vector3.Lerp(currentPos, targetPos, lerpSpeed * Time.deltaTime);
                        npc.transform.position = newPos;
                    }
                }

                if (targetRotations.TryGetValue(npcId, out Vector3 targetRot))
                {
                    Quaternion currentRot = npc.transform.rotation;
                    Quaternion targetQuaternion = Quaternion.Euler(targetRot);
                    npc.transform.rotation = Quaternion.Lerp(currentRot, targetQuaternion, 5f * Time.deltaTime);
                }
            }
        }

        private void SendPositionUpdates()
        {
            foreach (var kvp in idToNpc)
            {
                var npc = kvp.Value;
                var npcId = kvp.Key;

                if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                if (npc.health <= 0) continue; // Don't sync dead NPC positions

                Vector3 currentPos = npc.transform.position;

                // Check if NPC has moved significantly
                bool shouldSend = false;
                if (lastSentPositions.TryGetValue(npcId, out Vector3 lastPos))
                {
                    float distance = Vector3.Distance(currentPos, lastPos);
                    if (distance >= MIN_MOVEMENT_DISTANCE)
                    {
                        shouldSend = true;
                    }
                }
                else
                {
                    shouldSend = true; // First time sending
                }

                // Check timing throttle
                if (shouldSend && lastPositionSend.TryGetValue(npcId, out float lastTime))
                {
                    if (Time.time - lastTime < POSITION_SEND_INTERVAL)
                        shouldSend = false;
                }

                if (shouldSend)
                {
                    // Update tracking
                    lastSentPositions[npcId] = currentPos;
                    lastPositionSend[npcId] = Time.time;

                    // Update our state tracking
                    npcStates[npcId] = new NpcState
                    {
                        position = currentPos,
                        rotation = npc.transform.eulerAngles,
                        health = npc.health,
                        isDead = npc.health <= 0
                    };

                    net.SendMessage(new NpcPositionMessage
                    {
                        SceneName = SceneManager.GetActiveScene().name,
                        ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                        NpcId = npcId,
                        Position = currentPos,
                        Rotation = npc.transform.eulerAngles
                    });

                    Plugin.Log.LogDebug($"[NpcSync] Sent position update for NPC {npcId} ({npc.name}): {currentPos}");
                }
            }
        }

        // Send full scene state to newly connected client
        private void OnClientConnected()
        {
            if (!net.IsHost || !sceneReady) return;

            Plugin.Log.LogInfo("[NpcScanner] Client connected, sending scene state");

            StartCoroutine(SendSceneStateDelayed());
        }

        private IEnumerator SendSceneStateDelayed()
        {
            yield return new WaitForSeconds(1f); // Wait for client to be ready

            string sceneName = SceneManager.GetActiveScene().name;
            int zoneIndex = SceneManager.GetActiveScene().buildIndex;

            foreach (var kvp in idToNpc)
            {
                var npc = kvp.Value;
                var npcId = kvp.Key;

                if (npc == null) continue;

                // Send health state
                net.SendMessage(new NpcUpdateMessage
                {
                    SceneName = sceneName,
                    ZoneIndex = zoneIndex,
                    NpcId = npcId,
                    HP = Mathf.RoundToInt(npc.health),
                    Dead = (npc.health <= 0)
                });

                // Send position state (if alive)
                if (npc.health > 0 && npc.gameObject.activeInHierarchy)
                {
                    net.SendMessage(new NpcPositionMessage
                    {
                        SceneName = sceneName,
                        ZoneIndex = zoneIndex,
                        NpcId = npcId,
                        Position = npc.transform.position,
                        Rotation = npc.transform.eulerAngles
                    });
                }

                yield return new WaitForSeconds(0.1f); // Spread out the messages
            }

            Plugin.Log.LogInfo($"[NpcScanner] Finished sending scene state ({idToNpc.Count} NPCs)");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneReady = false;
            // Clear interpolation data
            targetPositions.Clear();
            targetRotations.Clear();
            positionLerpSpeed.Clear();
            npcStates.Clear();

            StartCoroutine(DelayedScan(scene));
        }

        private IEnumerator DelayedScan(Scene scene)
        {
            yield return new WaitForSeconds(2f);
            ScanScene(scene);
            sceneReady = true;

            // Request scene state if we're a client
            if (net?.IsClient == true)
            {
                yield return new WaitForSeconds(0.5f);
                RequestSceneState();
            }
        }

        private void RequestSceneState()
        {
            Plugin.Log.LogInfo("[NpcScanner] Client requesting scene state from host");

            net.SendMessage(new SceneQueryMessage
            {
                SceneName = SceneManager.GetActiveScene().name
            });
        }

        private void ScanScene(Scene scene)
        {
            idToNpc.Clear();
            npcToId.Clear();
            lastUpdateTime.Clear();
            lastHealthValue.Clear();
            lastSentPositions.Clear();
            lastPositionSend.Clear();

            var npcs = GameObject.FindObjectsOfType<AI_simple>(true);
            Plugin.Log.LogInfo($"[NpcScanner] Scene {scene.name}: Found {npcs.Length} NPCs");

            for (int i = 0; i < npcs.Length; i++)
            {
                idToNpc[i] = npcs[i];
                npcToId[npcs[i]] = i;
                lastHealthValue[i] = npcs[i].health;
                lastSentPositions[i] = npcs[i].transform.position;

                Plugin.Log.LogInfo($"[NpcScanner] ID {i} -> {npcs[i].name} (HP {npcs[i].health}/{npcs[i].health_max}) at {npcs[i].transform.position}");
            }
        }

        private void HandleMessage(NetworkMessage m)
        {
            if (!sceneReady) return;

            Plugin.Log.LogDebug($"[NpcScanner] Received message: {m.Kind}");

            switch (m)
            {
                case NpcUpdateMessage u:
                    HandleHealthUpdate(u);
                    break;
                case NpcPositionMessage p:
                    HandlePositionUpdate(p);
                    break;
                case SceneQueryMessage q:
                    if (net.IsHost) OnClientConnected(); // Send scene state
                    break;
            }
        }

        private void HandleHealthUpdate(NpcUpdateMessage u)
        {
            var currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != u.SceneName)
            {
                Plugin.Log.LogDebug($"[NpcScanner] Ignoring health update for different scene: {u.SceneName} (current: {currentScene})");
                return;
            }

            if (idToNpc.TryGetValue(u.NpcId, out var npc) && npc != null)
            {
                Plugin.Log.LogInfo($"[NpcScanner] Applying health update: ID={u.NpcId} ({npc.name}) HP={u.HP} Dead={u.Dead}");

                npc.health = u.HP;
                lastHealthValue[u.NpcId] = u.HP;

                // Update state tracking
                if (npcStates.ContainsKey(u.NpcId))
                {
                    var state = npcStates[u.NpcId];
                    state.health = u.HP;
                    state.isDead = u.Dead;
                    npcStates[u.NpcId] = state;
                }

                if (u.Dead && npc.health <= 0 && npc.gameObject.activeInHierarchy)
                {
                    Plugin.Log.LogInfo($"[NpcScanner] Killing NPC {u.NpcId} ({npc.name})");
                    npc.Die();
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[NpcScanner] No NPC found for ID={u.NpcId} in scene {u.SceneName}");
            }
        }

        private void HandlePositionUpdate(NpcPositionMessage p)
        {
            var currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != p.SceneName)
            {
                Plugin.Log.LogDebug($"[NpcScanner] Ignoring position update for different scene: {p.SceneName} (current: {currentScene})");
                return;
            }

            if (idToNpc.TryGetValue(p.NpcId, out var npc) && npc != null)
            {
                // Don't apply position updates to dead NPCs
                if (npc.health <= 0) return;

                Plugin.Log.LogDebug($"[NpcScanner] Received position update: ID={p.NpcId} ({npc.name}) Pos={p.Position}");

                // Set target position for interpolation instead of direct assignment
                targetPositions[p.NpcId] = p.Position;
                targetRotations[p.NpcId] = p.Rotation;

                // Calculate appropriate lerp speed based on distance
                float distance = Vector3.Distance(npc.transform.position, p.Position);
                float lerpSpeed = Mathf.Clamp(distance / 2f, 1f, 10f); // Adaptive speed
                positionLerpSpeed[p.NpcId] = lerpSpeed;

                // Update state tracking
                if (npcStates.ContainsKey(p.NpcId))
                {
                    var state = npcStates[p.NpcId];
                    state.position = p.Position;
                    state.rotation = p.Rotation;
                    npcStates[p.NpcId] = state;
                }
                else
                {
                    npcStates[p.NpcId] = new NpcState
                    {
                        position = p.Position,
                        rotation = p.Rotation,
                        health = npc.health,
                        isDead = npc.health <= 0
                    };
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[NpcScanner] No NPC found for position update ID={p.NpcId} in scene {p.SceneName}");
            }
        }

        public int GetNpcId(AI_simple npc)
        {
            if (npcToId.TryGetValue(npc, out var id)) return id;
            return -1;
        }

        // Check if we should send a health update for this NPC (throttling)
        public bool ShouldSendHealthUpdate(int npcId, float currentHealth)
        {
            float now = Time.time;

            // Always send death updates immediately
            if (currentHealth <= 0) return true;

            // Check if enough time has passed
            if (lastUpdateTime.TryGetValue(npcId, out float lastTime))
            {
                if (now - lastTime < MIN_UPDATE_INTERVAL) return false;
            }

            // Check if health changed significantly (more than 5 HP or 10%)
            if (lastHealthValue.TryGetValue(npcId, out float lastHealth))
            {
                float healthDiff = Mathf.Abs(currentHealth - lastHealth);
                float healthPercent = healthDiff / Mathf.Max(1f, lastHealth);

                if (healthDiff < 5f && healthPercent < 0.1f) return false;
            }

            // Update tracking values
            lastUpdateTime[npcId] = now;
            lastHealthValue[npcId] = currentHealth;
            return true;
        }
    }

    // Updated patch for health syncing
    [HarmonyPatch(typeof(OBJ_HEALTH), nameof(OBJ_HEALTH.Hurt))]
    class Patch_OBJ_HEALTH_Hurt
    {
        static void Postfix(OBJ_HEALTH __instance)
        {
            if (NetworkManager.Instance?.IsConnected != true) return;
            if (!NetworkManager.Instance.IsHost) return; // Only host sends updates
            if (NpcScanner.Instance == null) return;

            var ai = __instance.MOM?.GetComponent<AI_simple>();
            if (ai == null) return;

            int id = NpcScanner.Instance.GetNpcId(ai);
            if (id < 0) return;

            // Use throttling to avoid spam
            if (!NpcScanner.Instance.ShouldSendHealthUpdate(id, ai.health)) return;

            NetworkManager.Instance.SendMessage(new NpcUpdateMessage
            {
                SceneName = SceneManager.GetActiveScene().name,
                ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                NpcId = id,
                HP = Mathf.RoundToInt(ai.health),
                Dead = (ai.health <= 0)
            });

            Plugin.Log.LogDebug($"[NpcSync] Sent health update for NPC {id} ({ai.name}): HP={ai.health}");
        }
    }

    // Updated death patch
    [HarmonyPatch(typeof(AI_simple), nameof(AI_simple.Die))]
    class Patch_AI_Die
    {
        static void Postfix(AI_simple __instance)
        {
            if (NetworkManager.Instance?.IsConnected != true) return;
            if (!NetworkManager.Instance.IsHost) return;
            if (NpcScanner.Instance == null) return;

            int id = NpcScanner.Instance.GetNpcId(__instance);
            if (id < 0) return;

            NetworkManager.Instance.SendMessage(new NpcUpdateMessage
            {
                SceneName = SceneManager.GetActiveScene().name,
                ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                NpcId = id,
                HP = 0,
                Dead = true
            });

            Plugin.Log.LogInfo($"[NpcSync] Sent DEATH update for NPC {id} ({__instance.name})");
        }
    }
}