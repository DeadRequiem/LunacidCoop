using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;
using Steamworks;

namespace LunacidCoopMod
{
    // Host-authoritative NPC sync. NpcId is a deterministic sibling-index path.
    // Host streams position/health/attacks/death; client runs NPCs in puppet mode.
    public class NpcScanner : MonoBehaviour
    {
        private const float MIN_UPDATE_INTERVAL = 0.1f;       // health throttle
        private const float POSITION_SEND_INTERVAL = 0.15f;   // position throttle
        private const float MIN_MOVEMENT_DISTANCE = 1.0f;

        public static NpcScanner Instance;

        private Dictionary<string, AI_simple> idToNpc = new Dictionary<string, AI_simple>();
        private Dictionary<AI_simple, string> npcToId = new Dictionary<AI_simple, string>();

        private Dictionary<string, float> lastUpdateTime = new Dictionary<string, float>();
        private Dictionary<string, float> lastHealthValue = new Dictionary<string, float>();

        // Position interpolation (client side).
        private Dictionary<string, Vector3> targetPositions = new Dictionary<string, Vector3>();
        private Dictionary<string, Vector3> targetRotations = new Dictionary<string, Vector3>();
        private Dictionary<string, float> positionLerpSpeed = new Dictionary<string, float>();

        // Cached at scan time; GetChild(0).GetComponent would otherwise run per NPC per tick.
        private Dictionary<string, Animation> npcAnim = new Dictionary<string, Animation>();

        private Dictionary<string, Vector3> lastSentPositions = new Dictionary<string, Vector3>();
        // Facing and animation ride on the position message, so they need their own change gates.
        private Dictionary<string, Vector3> lastSentRotations = new Dictionary<string, Vector3>();
        private Dictionary<string, string>  lastSentClips     = new Dictionary<string, string>();
        private const float MIN_ROTATION_DEGREES = 15f;
        private Dictionary<string, float> lastPositionSend = new Dictionary<string, float>();
        private float lastPositionBatchSend = 0f;

        // Snapshot for newly-joined clients.
        private Dictionary<string, NpcState> npcStates = new Dictionary<string, NpcState>();

        // Client puppet mode: when each NPC is locked into a mirrored attack.
        private Dictionary<string, float> clientBusyUntil = new Dictionary<string, float>();

        private SteamNetworkManager net;
        private bool sceneReady = false;

        // Damage suppression needs to know whether the id map is populated yet.
        public bool SceneReady => sceneReady;

        // AI_simple.Flinch is private, and the client never runs the Hurt that would call it.
        private static readonly System.Reflection.MethodInfo FlinchMethod =
            AccessTools.Method(typeof(AI_simple), "Flinch");

        // OBJ_HEALTH.BURN and the HURTED guard the game checks before calling it are both private.
        private static readonly System.Reflection.MethodInfo BurnMethod =
            AccessTools.Method(typeof(OBJ_HEALTH), "BURN");
        private static readonly System.Reflection.FieldInfo HurtedField =
            AccessTools.Field(typeof(OBJ_HEALTH), "HURTED");

        // Resolve a part by path, optionally falling back to the first OBJ_HEALTH. Shared by the
        // damage, part and status handlers so all three agree on what a path means.
        internal static OBJ_HEALTH ResolvePart(AI_simple npc, string partPath, bool allowFallback)
        {
            if (npc == null) return null;
            OBJ_HEALTH oh = null;
            if (!string.IsNullOrEmpty(partPath))
            {
                var t = ScenePath.ResolveRelativePath(partPath, npc.transform);
                if (t != null) oh = t.GetComponent<OBJ_HEALTH>();
            }
            if (oh == null && allowFallback) oh = npc.GetComponentInChildren<OBJ_HEALTH>(includeInactive: true);
            return oh;
        }

        // One snapshot per target at a time; three call sites can otherwise overlap.
        private readonly Dictionary<ulong, Coroutine> activeSnapshots = new Dictionary<ulong, Coroutine>();

        // Host: askers whose SceneQuery arrived while we were still scanning, answered once ready.
        private readonly List<string> deferredQueries = new List<string>();

        // Client: retry state for the scene-state handshake.
        private bool sceneStateAnswered;
        private Coroutine sceneStateRetry;

        // Client: was the host in our scene last frame? Used to catch them leaving.
        private bool hostWasPresent;

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

            SyncHandler.Subscribe<NpcUpdateMessage>(HandleHealthUpdate);
            SyncHandler.Subscribe<NpcPositionMessage>(HandlePositionUpdate);
            SyncHandler.Subscribe<NpcAttackMessage>(HandleAttackMessage);
            SyncHandler.Subscribe<NpcDamageMessage>(HandleDamageMessage);
            SyncHandler.Subscribe<NpcPartMessage>(HandlePartUpdate);
            SyncHandler.Subscribe<NpcKillMessage>(HandleKillRequest);
            SyncHandler.Subscribe<NpcStatusMessage>(HandleStatusUpdate);
            SyncHandler.Subscribe<SceneQueryMessage>(HandleSceneQuery);

            // OnPeerJoined fires per-client so each new joiner receives scene state (OnConnected only fires once).
            net = SteamNetworkManager.Instance;
            if (net != null)
            {
                net.OnPeerJoined += OnPeerJoined;
                net.OnConnected  += OnBecameClient;
            }

            // Re-request scene state when the host arrives after our DelayedScan completed.
            if (PlayerSyncManager.Instance != null)
                PlayerSyncManager.Instance.OnPeerEnteredOurScene += OnPeerEnteredOurScene;

            CoopLog.NpcScanner("Initialized");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (net != null)
            {
                net.OnPeerJoined -= OnPeerJoined;
                net.OnConnected  -= OnBecameClient;
            }
            if (PlayerSyncManager.Instance != null)
                PlayerSyncManager.Instance.OnPeerEnteredOurScene -= OnPeerEnteredOurScene;
        }

        // Send scene state only to the newly-joined client; existing clients already have it.
        private void OnPeerJoined(ClientSlot slot)
        {
            if (net == null || !net.IsHost || !sceneReady) return;
            CoopLog.NpcScanner($"Client connected at slot {slot.Slot}, sending scene state");
            StartSnapshot(slot.SteamId);
        }

        // A friends-list or overlay invite loads no scene, so DelayedScan never runs.
        private void OnBecameClient()
        {
            if (net == null || !net.IsClient || !sceneReady) return;
            CoopLog.NpcScanner("Became a client mid-scene; freezing NPCs that were already running");
            FreezeClientNpcs();
        }

        private void Update()
        {
            if (!sceneReady) return;

            if (net?.IsClient == true)
            {
                bool hostHere = HostIsHere();
                if (hostWasPresent && !hostHere) ReleaseNpcsOnHostDeparture();
                hostWasPresent = hostHere;

                InterpolateNpcPositions();
            }

            if (net?.IsHost == true && Time.time - lastPositionBatchSend >= POSITION_SEND_INTERVAL)
            {
                SendPositionUpdates();
                lastPositionBatchSend = Time.time;
            }
        }

        private void InterpolateNpcPositions()
        {
            foreach (var kvp in idToNpc)
            {
                var npc = kvp.Value;
                var npcId = kvp.Key;

                if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                if (npc.health <= 0) continue;

                if (targetPositions.TryGetValue(npcId, out Vector3 targetPos))
                {
                    float lerpSpeed = positionLerpSpeed.TryGetValue(npcId, out float speed) ? speed : 2f;
                    Vector3 currentPos = npc.transform.position;

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
                if (npc.health <= 0) continue;

                // Catches HP the Hurt path threw away. It throttles small changes and does not retransmit.
                int hp = Mathf.RoundToInt(npc.health);
                if (!lastHealthValue.TryGetValue(npcId, out float lastHp) || Mathf.RoundToInt(lastHp) != hp)
                {
                    lastHealthValue[npcId] = npc.health;
                    SyncHandler.Send(new NpcUpdateMessage
                    {
                        SceneName = SceneManager.GetActiveScene().name,
                        ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                        NpcId     = npcId,
                        HP        = hp,
                        HealthMax = npc.health_max,
                        Dead      = false,
                        Undeth    = npc.UNDETH
                    });
                }

                Vector3 currentPos = npc.transform.position;
                Vector3 currentRot = npc.transform.eulerAngles;

                // Read before the gate. An NPC can turn or change clip without covering a metre.
                string clipName = "";
                float clipSpeed = 1f;
                npcAnim.TryGetValue(npcId, out var animComp);
                if (animComp != null)
                {
                    foreach (AnimationState state in animComp)
                    {
                        if (state.enabled && state.weight > 0.1f)
                        {
                            clipName = state.name;
                            clipSpeed = state.speed;
                            break;
                        }
                    }
                }

                bool shouldSend = false;
                if (!lastSentPositions.TryGetValue(npcId, out Vector3 lastPos))
                {
                    shouldSend = true;
                }
                else if (Vector3.Distance(currentPos, lastPos) >= MIN_MOVEMENT_DISTANCE)
                {
                    shouldSend = true;
                }
                else if (!lastSentRotations.TryGetValue(npcId, out Vector3 lastRot)
                         || Quaternion.Angle(Quaternion.Euler(currentRot), Quaternion.Euler(lastRot)) >= MIN_ROTATION_DEGREES)
                {
                    shouldSend = true;
                }
                else if (!lastSentClips.TryGetValue(npcId, out string lastClip) || lastClip != clipName)
                {
                    shouldSend = true;
                }

                if (shouldSend && lastPositionSend.TryGetValue(npcId, out float lastTime))
                {
                    if (Time.time - lastTime < POSITION_SEND_INTERVAL)
                        shouldSend = false;
                }

                if (shouldSend)
                {
                    lastSentPositions[npcId] = currentPos;
                    lastSentRotations[npcId] = currentRot;
                    lastSentClips[npcId]     = clipName;
                    lastPositionSend[npcId] = Time.time;

                    npcStates[npcId] = new NpcState
                    {
                        position = currentPos,
                        rotation = npc.transform.eulerAngles,
                        health = npc.health,
                        isDead = npc.health <= 0
                    };

                    SyncHandler.Send(new NpcPositionMessage
                    {
                        SceneName = SceneManager.GetActiveScene().name,
                        ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                        NpcId = npcId,
                        Position = currentPos,
                        Rotation = npc.transform.eulerAngles,
                        ClipName = clipName,
                        ClipSpeed = clipSpeed
                    });
                }
            }
        }

        // Single-flight per target. OnPeerJoined, HandleSceneQuery and the deferred flush can all
        // fire for the same client, and two overlapping snapshots just duplicate the whole burst.
        private void StartSnapshot(CSteamID target)
        {
            ulong key = target.m_SteamID;
            if (activeSnapshots.TryGetValue(key, out var running) && running != null) return;
            activeSnapshots[key] = StartCoroutine(SendSceneStateDelayed(target));
        }

        // Send the full scene state to one client (initial connect or post-scene-change query).
        private IEnumerator SendSceneStateDelayed(CSteamID target)
        {
            yield return new WaitForSeconds(1f);

            string sceneName = SceneManager.GetActiveScene().name;
            int zoneIndex = SceneManager.GetActiveScene().buildIndex;

            // Copy first. A scene load repopulates idToNpc mid-iteration and invalidates the enumerator.
            var entries = new List<KeyValuePair<string, AI_simple>>(idToNpc);

            int alive = 0, dead = 0;
            foreach (var kvp in entries)
            {
                // After a scene load every captured NPC is destroyed and the null branch below reports them dead.
                if (SceneManager.GetActiveScene().name != sceneName) yield break;

                var npc = kvp.Value;
                var npcId = kvp.Key;

                // Destroyed AI_simple: still send the death packet so joiners kill their local copy too.
                if (npc == null)
                {
                    net.SendTo(new NpcUpdateMessage
                    {
                        SceneName = sceneName,
                        ZoneIndex = zoneIndex,
                        NpcId = npcId,
                        HP = 0,
                        Dead = true,
                        Snapshot = true
                    }, target);
                    dead++;
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }

                net.SendTo(new NpcUpdateMessage
                {
                    SceneName = sceneName,
                    ZoneIndex = zoneIndex,
                    NpcId = npcId,
                    HP = Mathf.RoundToInt(npc.health),
                    HealthMax = npc.health_max,
                    // AI_simple only dies at "health <= 0 && !UNDETH"; inferring death from health
                    // alone tells the client to destroy an enemy the host keeps alive.
                    Dead = (npc.health <= 0 && !npc.UNDETH),
                    Undeth = npc.UNDETH,
                    Snapshot = true
                }, target);

                // Part health isn't carried by any other message.
                foreach (var oh in npc.GetComponentsInChildren<OBJ_HEALTH>(includeInactive: true))
                {
                    if (oh == null || oh.type != 2) continue;
                    net.SendTo(new NpcPartMessage
                    {
                        SceneName = sceneName,
                        NpcId     = npcId,
                        PartPath  = ScenePath.BuildRelativePath(oh.transform, npc.transform),
                        Health    = oh.Health,
                        Dead      = oh.Health <= 0f
                    }, target);
                }

                if (npc.health > 0 && npc.gameObject.activeInHierarchy)
                {
                    net.SendTo(new NpcPositionMessage
                    {
                        SceneName = sceneName,
                        ZoneIndex = zoneIndex,
                        NpcId = npcId,
                        Position = npc.transform.position,
                        Rotation = npc.transform.eulerAngles
                    }, target);
                }
                alive++;

                yield return new WaitForSeconds(0.1f);
            }

            activeSnapshots.Remove(target.m_SteamID);
            CoopLog.NpcScanner($"Finished sending scene state to {target} ({alive} alive, {dead} dead)");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneReady = false;
            deferredQueries.Clear();
            sceneStateAnswered = false;
            hostWasPresent = false;
            foreach (var co in activeSnapshots.Values)
                if (co != null) StopCoroutine(co);
            activeSnapshots.Clear();
            if (sceneStateRetry != null) { StopCoroutine(sceneStateRetry); sceneStateRetry = null; }
            targetPositions.Clear();
            targetRotations.Clear();
            positionLerpSpeed.Clear();
            npcStates.Clear();
            clientBusyUntil.Clear();

            StartCoroutine(DelayedScan(scene));
        }

        private IEnumerator DelayedScan(Scene scene)
        {
            yield return new WaitForSeconds(2f);
            ScanScene(scene);
            sceneReady = true;

            // Answer anyone who asked mid-scan.
            if (net?.IsHost == true && deferredQueries.Count > 0)
            {
                var pending = new List<string>(deferredQueries);
                deferredQueries.Clear();
                foreach (var askerId in pending)
                {
                    if (net.HostSlots.TryGetByPlayerId(askerId, out var slot))
                    {
                        CoopLog.NpcScanner($"Answering deferred SceneQuery from slot {slot.Slot}");
                        StartSnapshot(slot.SteamId);
                    }
                }
            }

            // Client: always freeze NPCs (host-authoritative). Request state now or wait for host's arrival.
            if (net?.IsClient == true)
            {
                FreezeClientNpcs();

                if (PlayerSyncManager.Instance?.HostDummy != null)
                {
                    yield return new WaitForSeconds(0.5f);
                    RequestSceneStateUntilAnswered();
                }
                else
                {
                    CoopLog.NpcScanner("Host not in our scene - NPCs frozen until host arrives");
                }
            }
        }

        // Host arrived after our scene-load: NPCs were frozen by DelayedScan; now sync their state.
        private void OnPeerEnteredOurScene(string playerId)
        {
            if (net == null || net.IsHost) return;
            if (!sceneReady) return;  // DelayedScan will pick this up itself
            if (!net.TryGetHostPlayerId(out string hostPid)) return;
            if (playerId != hostPid) return;

            CoopLog.NpcScanner("Host entered our scene; requesting fresh state");
            RequestSceneStateUntilAnswered();
        }

        // Stop client-side AI so NPCs become host puppets. NPC_damage stays enabled for mirrored attacks.
        private void FreezeClientNpcs()
        {
            // Reset. An NPC alerted before the scan is already Aggressive and Update would undo it.
            foreach (var kvp in idToNpc)
                ResetNpcToIdle(kvp.Value);
            CoopLog.NpcScanner($"Froze {idToNpc.Count} NPCs for client puppet mode");
        }

        // Put a locally-woken NPC back to sleep
        private void ResetNpcToIdle(AI_simple npc)
        {
            if (npc == null) return;
            if (npc.Alerted)
            {
                bool docile = npc.DOCILE;
                npc.Forget();
                npc.DOCILE = docile;
            }
            FreezeNpcForClient(npc);
        }

        private bool HostIsHere()
        {
            if (net == null || net.IsHost) return true;
            var psm = PlayerSyncManager.Instance;
            if (psm == null) return false;
            return net.TryGetHostPlayerId(out string hostPid) && psm.GetDummy(hostPid) != null;
        }

        private void ReleaseNpcsOnHostDeparture()
        {
            int released = 0;
            foreach (var npc in TrackedNpcs())
            {
                if (npc == null || npc.health <= 0) continue;
                ResetNpcToIdle(npc);
                released++;
            }
            CoopLog.NpcScanner($"Host left our scene; released {released} NPCs");
        }

        private void FreezeNpcForClient(AI_simple npc)
        {
            if (npc == null) return;

            npc.StopAllCoroutines();

            var agent = npc.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null)
            {
                agent.isStopped = true;
                agent.updateRotation = false;
                agent.velocity = Vector3.zero;
            }
        }

        // Keep asking until the snapshot starts arriving; one shot can be lost or deferred.
        private void RequestSceneStateUntilAnswered()
        {
            if (sceneStateRetry != null) StopCoroutine(sceneStateRetry);
            sceneStateRetry = StartCoroutine(SceneStateRetryLoop());
        }

        private IEnumerator SceneStateRetryLoop()
        {
            const int MAX_ATTEMPTS = 6;
            const float RETRY_INTERVAL = 2.5f;

            sceneStateAnswered = false;
            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                if (sceneStateAnswered || net?.IsClient != true) yield break;

                RequestSceneState();
                CoopLog.NpcScanner($"Scene-state request attempt {attempt}/{MAX_ATTEMPTS}");

                float waited = 0f;
                while (waited < RETRY_INTERVAL)
                {
                    if (sceneStateAnswered) yield break;
                    waited += Time.deltaTime;
                    yield return null;
                }
            }
            Plugin.Log.LogWarning("[NpcScanner] Host never answered the scene-state request; " +
                                  "NPCs it already killed may still appear alive here.");
        }

        private void RequestSceneState()
        {
            CoopLog.NpcScanner("Client requesting scene state from host");
            SyncHandler.Send(new SceneQueryMessage
            {
                SceneName     = SceneManager.GetActiveScene().name,
                AskerPlayerId = net?.LocalPlayerId ?? ""
            });
        }

        private void ScanScene(Scene scene)
        {
            idToNpc.Clear();
            npcToId.Clear();
            npcAnim.Clear();
            lastUpdateTime.Clear();
            lastHealthValue.Clear();
            lastSentPositions.Clear();
            lastSentRotations.Clear();
            lastSentClips.Clear();
            lastPositionSend.Clear();

            var npcs = GameObject.FindObjectsOfType<AI_simple>(true);
            CoopLog.NpcScanner($"Scene {scene.name}: Found {npcs.Length} NPCs");

            foreach (var npc in npcs)
            {
                // Sibling-index path; resolves to the same NPC on both ends of the wire.
                string id = ScenePath.BuildScenePath(npc.transform);

                if (idToNpc.ContainsKey(id))
                {
                    Plugin.Log.LogWarning($"[NpcScanner] Duplicate path for NPC: {id} - skipping");
                    continue;
                }

                idToNpc[id] = npc;
                npcToId[npc] = id;
                npcAnim[id] = npc.transform.childCount > 0
                    ? npc.transform.GetChild(0).GetComponent<Animation>()
                    : null;
                lastHealthValue[id] = npc.health;
                lastSentPositions[id] = npc.transform.position;

                Plugin.Log.LogDebug($"[NpcScanner] {id} -> {npc.name} (HP {npc.health}/{npc.health_max})");
            }
        }

        // --- Message handlers ----------

        private void HandleSceneQuery(SceneQueryMessage q)
        {
            if (net == null || !net.IsHost) return;

            // Both players usually change scene together, so requests routinely land mid-scan.
            // Dropping one used to be permanent, so defer it instead.
            if (!sceneReady)
            {
                if (!string.IsNullOrEmpty(q.AskerPlayerId) && !deferredQueries.Contains(q.AskerPlayerId))
                    deferredQueries.Add(q.AskerPlayerId);
                CoopLog.NpcScanner($"SceneQuery from {q.AskerPlayerId} arrived mid-scan; deferred until ready");
                return;
            }

            // Only respond if we're in the asker's scene; mismatched IDs would collide locally.
            string myScene = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(q.SceneName) && q.SceneName != myScene)
            {
                CoopLog.NpcScanner($"Client asked for scene '{q.SceneName}' but host is in '{myScene}' - ignoring");
                return;
            }

            // Asker must still be a tracked peer; silently drop otherwise.
            if (string.IsNullOrEmpty(q.AskerPlayerId))
            {
                CoopLog.NpcScanner("SceneQueryMessage missing AskerPlayerId; ignoring");
                return;
            }
            if (!net.HostSlots.TryGetByPlayerId(q.AskerPlayerId, out var askerSlot))
            {
                CoopLog.NpcScanner($"SceneQuery from unknown PlayerId {q.AskerPlayerId}; ignoring");
                return;
            }
            CoopLog.NpcScanner($"Client at slot {askerSlot.Slot} requested scene state");
            StartSnapshot(askerSlot.SteamId);
        }

        private void HandleAttackMessage(NpcAttackMessage a)
        {
            if (!sceneReady) return;
            if (SceneManager.GetActiveScene().name != a.SceneName) return;

            if (!idToNpc.TryGetValue(a.NpcId, out var npc) || npc == null) return;
            if (npc.health <= 0) return;
            if (a.BehaviorIndex < 0 || a.BehaviorIndex >= npc.ABH.Length) return;

            Plugin.Log.LogDebug($"[NpcSync] Mirroring attack NPC {a.NpcId} ({npc.name}) behavior {a.BehaviorIndex}");

            // Block position-message anim overwrite for the duration of the attack clip.
            float attackLength = npc.ABH[a.BehaviorIndex].length;
            if (attackLength < 0f && npc.ABH[a.BehaviorIndex].anim != null)
                attackLength = npc.ABH[a.BehaviorIndex].anim.length;
            clientBusyUntil[a.NpcId] = Time.time + Mathf.Max(attackLength, 0.5f);

            // Run_Aggro plays the attack and arms hitboxes so the local player can be hit.
            npc.Run_Aggro(a.BehaviorIndex);

            // Run_Aggro re-enables the agent; re-freeze.
            var agent = npc.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null) agent.isStopped = true;
        }

        // Host applies a client-reported hit; Patch_OBJ_HEALTH_Hurt's Postfix broadcasts the new HP back.
        private void HandleDamageMessage(NpcDamageMessage d)
        {
            if (!sceneReady) return;
            if (net == null || !net.IsHost) return;
            // The only handler that lacked a scene check. Ids are sibling paths, so a report from a
            // client in another scene would otherwise land on whatever shares that path in ours.
            if (!string.IsNullOrEmpty(d.Scene) && d.Scene != SceneManager.GetActiveScene().name) return;

            if (!idToNpc.TryGetValue(d.NpcId, out var npc) || npc == null) return;
            if (npc.health <= 0) return;

            // Resolve the part the client actually hit; first-child was putting every report on one
            // part. Empty PartPath means an older peer or a body hit, so keep the old fallback.
            var oh = ResolvePart(npc, d.PartPath, allowFallback: true);
            if (oh == null)
            {
                Plugin.Log.LogWarning($"[NpcSync] Host got damage for NPC {d.NpcId} ({npc.name}) but no OBJ_HEALTH found");
                return;
            }

            oh.Hurt(new Vector2(d.Amount, d.Element));
            Plugin.Log.LogDebug($"[NpcSync] Host applied client damage to NPC {d.NpcId} ({npc.name}): {d.Amount} elem={d.Element}");
        }

        // Host applies a client kill request. Its own Patch_AI_Die postfix then broadcasts the death,
        // so every client converges through the normal path.
        private void HandleKillRequest(NpcKillMessage k)
        {
            if (!sceneReady) return;
            if (net == null || !net.IsHost) return;
            if (!string.IsNullOrEmpty(k.Scene) && k.Scene != SceneManager.GetActiveScene().name) return;

            if (!idToNpc.TryGetValue(k.NpcId, out var npc) || npc == null) return;
            if (npc.UNDETH) return;   // host decides; special 16 only clears this locally

            CoopLog.NpcSync("Applying client kill request for " + k.NpcId + " (" + npc.name + ")");
            npc.Die();
        }

        // Replay an elemental status the client suppressed Hurt never accumulated.
        private void HandleStatusUpdate(NpcStatusMessage m)
        {
            if (!sceneReady) return;
            if (net == null || net.IsHost) return;
            if (!string.IsNullOrEmpty(m.Scene) && m.Scene != SceneManager.GetActiveScene().name) return;
            if (BurnMethod == null) return;

            if (!idToNpc.TryGetValue(m.NpcId, out var npc) || npc == null) return;
            var oh = ResolvePart(npc, m.PartPath, allowFallback: true);
            if (oh == null) return;

            // BURN instantiates its effect unconditionally; the game guards on HURTED at the call
            // site, so mirror that or a repeat message stacks effects.
            if (HurtedField != null && HurtedField.GetValue(oh) != null) return;

            try { BurnMethod.Invoke(oh, new object[] { m.Which }); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[NpcSync] BURN replay failed: " + e.Message); }
        }

        private void HandleHealthUpdate(NpcUpdateMessage u)
        {
            if (!sceneReady) return;
            if (SceneManager.GetActiveScene().name != u.SceneName) return;

            if (!idToNpc.TryGetValue(u.NpcId, out var npc) || npc == null)
            {
                Plugin.Log.LogWarning($"[NpcScanner] No NPC found for ID={u.NpcId} in scene {u.SceneName}");
                return;
            }

            Plugin.Log.LogDebug($"[NpcScanner] Applying health update: ID={u.NpcId} ({npc.name}) HP={u.HP} Dead={u.Dead}");

            // Only a snapshot ends the retry. Ordinary combat traffic would satisfy it while the query is still lost.
            if (u.Snapshot) sceneStateAnswered = true;

            // Host is authoritative for the max; ours came from the local save's NPC_Scaling.
            if (u.HealthMax > 0f) npc.health_max = u.HealthMax;
            npc.UNDETH = u.Undeth;

            float previousHealth = npc.health;
            npc.health = u.HP;
            lastHealthValue[u.NpcId] = u.HP;

            // Drive HP bar + boss splash; our Hurt-prefix suppression means they wouldn't fire otherwise.
            if (u.HP < previousHealth)
            {
                var oh = npc.GetComponentInChildren<OBJ_HEALTH>(includeInactive: true);
                if (oh != null)
                {
                    var nb = oh.GetComponent<ShowNPCBar>();
                    if (nb != null) nb.Hurt();
                }

                if (npc.BAR != null)
                {
                    string translation = npc.gameObject.name;
                    if (!I2.Loc.LocalizationManager.TryGetTranslation("Enemies/" + translation, out translation))
                        translation = npc.gameObject.name;
                    npc.BAR.Splash(u.HP, npc.health_max, translation);
                }

                // Replay the rest of what AI_simple.Hurt would have done. Impact VFX survive the
                // suppression (Damage_Trigger spawns those outside Hurt), but these two do not.
                float delta = previousHealth - u.HP;
                if (npc.Gore != null) npc.Gore.Damage(delta);
                if (delta > npc.health_max / 5f && FlinchMethod != null)
                {
                    try { FlinchMethod.Invoke(npc, null); }
                    catch (System.Exception e) { Plugin.Log.LogWarning($"[NpcSync] Flinch replay failed: {e.Message}"); }
                }
            }

            if (npcStates.ContainsKey(u.NpcId))
            {
                var state = npcStates[u.NpcId];
                state.health = u.HP;
                state.isDead = u.Dead;
                npcStates[u.NpcId] = state;
            }

            // known issue that if a client leaves and re-enters a scene with dead npcs the client will
            // get additional loot and xp every time. Will look into later if decided important enough
            if (u.Dead && npc.health <= 0 && !npc.UNDETH && npc.gameObject.activeInHierarchy)
            {
                CoopLog.NpcScanner($"Killing NPC {u.NpcId} ({npc.name})");
                Patch_AI_Die.ApplyingRemote = true;
                try { npc.Die(); }
                finally { Patch_AI_Die.ApplyingRemote = false; }
            }
        }

        // AI_simple.health is the body total only, so without this a client's part bars sit at full
        // and parts the host destroyed stay intact on screen.
        private void HandlePartUpdate(NpcPartMessage m)
        {
            if (!sceneReady) return;
            if (net == null || net.IsHost) return;   // host is the authority; ignore its own echo
            if (!string.IsNullOrEmpty(m.SceneName) && SceneManager.GetActiveScene().name != m.SceneName) return;

            if (!idToNpc.TryGetValue(m.NpcId, out var npc) || npc == null) return;

            var t = ScenePath.ResolveRelativePath(m.PartPath, npc.transform);
            var oh = t != null ? t.GetComponent<OBJ_HEALTH>() : null;
            if (oh == null)
            {
                Plugin.Log.LogWarning($"[NpcSync] Part '{m.PartPath}' did not resolve on {npc.name}");
                return;
            }

            bool wasAlive = oh.Health > 0f;
            oh.Health = m.Health;

            var bar = oh.GetComponent<ShowNPCBar>();
            if (bar != null) bar.Hurt();

            // Patch_ObjHealth_Die skips type 1/2, so this can't echo back as a WorldDestroyMessage.
            if (m.Dead && wasAlive && oh.gameObject.activeInHierarchy)
                oh.Die();
        }

        private void HandlePositionUpdate(NpcPositionMessage p)
        {
            if (!sceneReady) return;
            if (SceneManager.GetActiveScene().name != p.SceneName) return;

            if (!idToNpc.TryGetValue(p.NpcId, out var npc) || npc == null)
            {
                Plugin.Log.LogWarning($"[NpcScanner] No NPC found for position update ID={p.NpcId}");
                return;
            }

            if (npc.health <= 0) return;

            targetPositions[p.NpcId] = p.Position;
            targetRotations[p.NpcId] = p.Rotation;

            float distance = Vector3.Distance(npc.transform.position, p.Position);
            positionLerpSpeed[p.NpcId] = Mathf.Clamp(distance / 2f, 1f, 10f);

            // Skip the anim apply while the NPC is mid-attack (attack message takes priority).
            if (!string.IsNullOrEmpty(p.ClipName))
            {
                bool isBusy = clientBusyUntil.TryGetValue(p.NpcId, out float busyTime) && Time.time < busyTime;
                if (!isBusy)
                {
                    npcAnim.TryGetValue(p.NpcId, out var animComp);
                    if (animComp != null && animComp[p.ClipName] != null)
                    {
                        animComp[p.ClipName].speed = p.ClipSpeed > 0f ? p.ClipSpeed : 1f;
                        animComp.CrossFade(p.ClipName, 0.15f);
                    }
                }
            }

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

        // Used by AIPatches to clear references to a dying dummy.
        public IEnumerable<AI_simple> TrackedNpcs()
        {
            foreach (var kvp in idToNpc)
                yield return kvp.Value;
        }

        public string GetNpcId(AI_simple npc)
        {
            return npcToId.TryGetValue(npc, out var id) ? id : null;
        }

        // Throttled per-NPC health-update gate. Death always passes.
        public bool ShouldSendHealthUpdate(string npcId, float currentHealth)
        {
            float now = Time.time;

            if (currentHealth <= 0) return true;

            if (lastUpdateTime.TryGetValue(npcId, out float lastTime))
            {
                if (now - lastTime < MIN_UPDATE_INTERVAL) return false;
            }

            if (lastHealthValue.TryGetValue(npcId, out float lastHealth))
            {
                float healthDiff = Mathf.Abs(currentHealth - lastHealth);
                float healthPercent = healthDiff / Mathf.Max(1f, lastHealth);

                if (healthDiff < 5f && healthPercent < 0.1f) return false;
            }

            lastUpdateTime[npcId] = now;
            lastHealthValue[npcId] = currentHealth;
            return true;
        }
    }

    // Bidirectional OBJ_HEALTH.Hurt patch. Client Prefix sends NpcDamageMessage to host;
    // host Postfix broadcasts the resulting HP to all clients. NPC-only (types 1/2).
    [HarmonyPatch(typeof(OBJ_HEALTH), nameof(OBJ_HEALTH.Hurt))]
    class Patch_OBJ_HEALTH_Hurt
    {
        // Return false to skip the local Hurt: client shouldn't mutate host-authoritative HP.
        static bool Prefix(OBJ_HEALTH __instance, Vector2 damage)
        {
            if (!SyncHandler.IsConnected) return true;
            if (SyncHandler.IsHost) return true;
            if (__instance.type != 1 && __instance.type != 2) return true;

            // Nothing has an id before the scan, so the fallbacks below would let local damage through.
            if (NpcScanner.Instance != null && !NpcScanner.Instance.SceneReady) return false;

            // No host present: suppress damage entirely so we don't desync the moment host arrives.
            if (PlayerSyncManager.Instance?.HostDummy == null) return false;

            var ai = __instance.MOM?.GetComponent<AI_simple>();
            if (ai == null) return true;
            if (NpcScanner.Instance == null) return true;

            string id = NpcScanner.Instance.GetNpcId(ai);
            if (string.IsNullOrEmpty(id)) return true;

            SyncHandler.Send(new NpcDamageMessage
            {
                Scene            = SceneManager.GetActiveScene().name,
                NpcId            = id,
                Amount           = damage.x,
                Element          = Mathf.RoundToInt(damage.y),
                AttackerPlayerId = PlayerSyncManager.Instance?.LocalPlayerId ?? "",
                PartPath         = ScenePath.BuildRelativePath(__instance.transform, ai.transform)
            });

            Plugin.Log.LogDebug($"[NpcSync] Client sent damage NPC {id} ({ai.name}): amount={damage.x} elem={damage.y}");
            return false;
        }

        static void Postfix(OBJ_HEALTH __instance)
        {
            if (!SyncHandler.IsConnected) return;
            if (!SyncHandler.IsHost) return;
            if (NpcScanner.Instance == null) return;

            var ai = __instance.MOM?.GetComponent<AI_simple>();
            if (ai == null) return;

            string id = NpcScanner.Instance.GetNpcId(ai);
            if (string.IsNullOrEmpty(id)) return;

            // Outside the body-HP throttle: a destroyed part must never be dropped.
            if (__instance.type == 2)
            {
                SyncHandler.Send(new NpcPartMessage
                {
                    SceneName = SceneManager.GetActiveScene().name,
                    NpcId     = id,
                    PartPath  = ScenePath.BuildRelativePath(__instance.transform, ai.transform),
                    Health    = __instance.Health,
                    Dead      = __instance.Health <= 0f
                });
            }

            if (!NpcScanner.Instance.ShouldSendHealthUpdate(id, ai.health)) return;

            SyncHandler.Send(new NpcUpdateMessage
            {
                SceneName = SceneManager.GetActiveScene().name,
                ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                NpcId = id,
                HP = Mathf.RoundToInt(ai.health),
                HealthMax = ai.health_max,
                Dead = (ai.health <= 0 && !ai.UNDETH),
                Undeth = ai.UNDETH
            });
        }
    }

    // Host's Run_Aggro broadcasts the attack so clients can mirror it.
    // Host side: elemental status fires inside OBJ_HEALTH.Hurt, which a client never runs.
    [HarmonyPatch(typeof(OBJ_HEALTH), "BURN")]
    class Patch_OBJ_HEALTH_Burn
    {
        static void Postfix(OBJ_HEALTH __instance, int which)
        {
            if (!SyncHandler.IsConnected || !SyncHandler.IsHost) return;
            if (NpcScanner.Instance == null) return;

            var ai = __instance.MOM?.GetComponent<AI_simple>();
            if (ai == null) return;

            string id = NpcScanner.Instance.GetNpcId(ai);
            if (string.IsNullOrEmpty(id)) return;

            SyncHandler.Send(new NpcStatusMessage
            {
                Scene    = SceneManager.GetActiveScene().name,
                NpcId    = id,
                PartPath = ScenePath.BuildRelativePath(__instance.transform, ai.transform),
                Which    = which
            });
        }
    }

    [HarmonyPatch(typeof(AI_simple), nameof(AI_simple.Run_Aggro))]
    class Patch_AI_RunAggro
    {
        static void Postfix(AI_simple __instance, int which)
        {
            if (!SyncHandler.IsConnected) return;
            if (!SyncHandler.IsHost) return;
            if (NpcScanner.Instance == null) return;

            string id = NpcScanner.Instance.GetNpcId(__instance);
            if (string.IsNullOrEmpty(id)) return;

            SyncHandler.Send(new NpcAttackMessage
            {
                SceneName = SceneManager.GetActiveScene().name,
                ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                NpcId = id,
                BehaviorIndex = which
            });
        }
    }

    [HarmonyPatch(typeof(AI_simple), nameof(AI_simple.Die))]
    class Patch_AI_Die
    {
        // Set while a client replays a death the host already reported.
        public static bool ApplyingRemote = false;

        // Weapon specials (special 21 on JOTUNN) call Die() directly, bypassing OBJ_HEALTH.Hurt.
        static bool Prefix(AI_simple __instance)
        {
            if (ApplyingRemote) return true;
            if (!SyncHandler.IsConnected || SyncHandler.IsHost) return true;
            if (NpcScanner.Instance == null) return true;

            string id = NpcScanner.Instance.GetNpcId(__instance);
            if (string.IsNullOrEmpty(id)) return true;   // untracked, local-only: leave it alone

            SyncHandler.Send(new NpcKillMessage
            {
                Scene = SceneManager.GetActiveScene().name,
                NpcId = id
            });
            CoopLog.NpcSync("Blocked local Die on " + __instance.name + "; asked host to kill " + id);
            return false;
        }

        static void Postfix(AI_simple __instance)
        {
            if (!SyncHandler.IsConnected) return;
            if (!SyncHandler.IsHost) return;
            if (NpcScanner.Instance == null) return;

            string id = NpcScanner.Instance.GetNpcId(__instance);
            if (string.IsNullOrEmpty(id)) return;

            SyncHandler.Send(new NpcUpdateMessage
            {
                SceneName = SceneManager.GetActiveScene().name,
                ZoneIndex = SceneManager.GetActiveScene().buildIndex,
                NpcId = id,
                HP = 0,
                HealthMax = __instance.health_max,
                Dead = true
            });

            CoopLog.NpcSync($"Sent DEATH update for NPC {id} ({__instance.name})");
        }
    }
}
