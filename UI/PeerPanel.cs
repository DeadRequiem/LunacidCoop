using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    // Bottom-left per-peer HUD: condition dot + name + ping + compass arrow/distance (same scene) or "(in SCENE)" (elsewhere).
    // Host pinned to the bottom row; others stack newest-on-top by JoinTime. Hidden with no peers.
    public class PeerPanel : MonoBehaviour
    {
        // --- Layout ----------
        // 240 before the condition dot; widened by exactly DOT_WIDTH + COL_GAP.
        private const float BOX_WIDTH     = 256f;
        private const float ROW_HEIGHT    = 24f;
        private const float BOX_PADDING   = 6f;
        private const float MARGIN_LEFT   = 24f;
        private const float MARGIN_BOTTOM = 60f;

        // Names truncate at MAX_NAME_CHARS; the right-hand columns are sized so "(in SCENE)" fits.
        private const float DOT_WIDTH     = 12f;
        private const float DOT_SIZE      = 8f;
        private const float NAME_WIDTH    = 80f;
        private const float PING_WIDTH    = 48f;
        private const float ARROW_WIDTH   = 24f;
        private const float DIST_WIDTH    = 60f;
        private const float COL_GAP       = 4f;
        private const float ARROW_SIZE    = 20f;
        private const int   MAX_NAME_CHARS = 11;

        // --- Colors ----------
        private static readonly Color BOX_BG     = new Color(0f, 0f, 0f, 0.6f);
        private static readonly Color DIM_GRAY   = new Color(0.7f, 0.7f, 0.7f, 0.85f);
        private static readonly Color AMBER_DIM  = new Color(0.95f, 0.7f, 0.3f, 0.9f);

        // Condition dot, high to low. DIM_GRAY covers "no health data yet".
        private static readonly Color HP_HEALTHY  = new Color(0.35f, 0.85f, 0.35f, 0.95f); // 75%+
        private static readonly Color HP_INJURED  = new Color(0.95f, 0.90f, 0.30f, 0.95f); // 50-74%
        private static readonly Color HP_SERIOUS  = new Color(1.00f, 0.60f, 0.15f, 0.95f); // 25-49%
        private static readonly Color HP_CRITICAL = new Color(0.90f, 0.20f, 0.20f, 0.95f); // 1-24%
        private static readonly Color HP_DEAD     = new Color(0.45f, 0.08f, 0.08f, 0.95f); // 0%

        public static PeerPanel Instance;

        // White triangle tinted per-peer at draw time.
        private Texture2D arrowTex;

        // White circle tinted per-peer by condition at draw time.
        private Texture2D dotTex;

        private GUIStyle nameStyle;
        private GUIStyle pingStyle;
        private GUIStyle distStyle;
        private GUIStyle sceneStyle;
        private bool stylesReady;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            arrowTex = BuildArrowTexture(32, Color.white);
            dotTex   = BuildDotTexture(32, Color.white);
        }

        private void OnDestroy()
        {
            if (arrowTex != null) Destroy(arrowTex);
            if (dotTex != null) Destroy(dotTex);
        }

        private static Texture2D BuildArrowTexture(int size, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, Color.clear);

            // Triangle pointing up in texture space.
            float c = (size - 1) * 0.5f;
            float halfBase = size * 0.32f;
            float baseY = size * 0.18f;
            float apexY = size * 0.85f;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (y < baseY || y > apexY) continue;
                    float t = (y - baseY) / (apexY - baseY);
                    float halfWidth = halfBase * (1f - t);
                    if (Mathf.Abs(x - c) <= halfWidth)
                        tex.SetPixel(x, y, color);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D BuildDotTexture(int size, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

            float c = (size - 1) * 0.5f;
            float r = size * 0.45f;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Feathered edge; the dot draws at a quarter of this size.
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(r - d);
                    tex.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * a));
                }
            tex.Apply();
            return tex;
        }

        // Negative fraction means PlayerSyncManager has no health for them yet.
        private static Color ConditionColor(float f)
        {
            if (f <  0f)    return DIM_GRAY;
            if (f <= 0f)    return HP_DEAD;
            if (f >= 0.75f) return HP_HEALTHY;
            if (f >= 0.50f) return HP_INJURED;
            if (f >= 0.25f) return HP_SERIOUS;
            return HP_CRITICAL;
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;

            nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                wordWrap  = false,
                clipping  = TextClipping.Clip
            };
            pingStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize  = 11,
                fontStyle = FontStyle.Normal,
                wordWrap  = false
            };
            distStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                wordWrap  = false
            };
            distStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 0.9f);

            sceneStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize  = 11,
                fontStyle = FontStyle.Italic,
                wordWrap  = false,
                clipping  = TextClipping.Clip
            };
            sceneStyle.normal.textColor = AMBER_DIM;

            stylesReady = true;
        }

        private void OnGUI()
        {
            if (Plugin.ShowPeerPanel == null || !Plugin.ShowPeerPanel.Value) return;
            if (!SyncHandler.IsConnected) return;

            var psm = PlayerSyncManager.Instance;
            var net = SteamNetworkManager.Instance;
            if (psm == null || net == null) return;

            var peers = CollectAndSortPeers(net);
            if (peers.Count == 0) return;

            var cam = Camera.main;
            if (cam == null) return;
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude < 0.01f) return;
            camFwd.Normalize();

            EnsureStyles();

            // Bottom-left anchored; height grows with peer count.
            int rowCount = peers.Count;
            float boxH = ROW_HEIGHT * rowCount + BOX_PADDING * 2f;
            float boxX = MARGIN_LEFT;
            float boxY = Screen.height - boxH - MARGIN_BOTTOM;

            var prevColor = GUI.color;
            GUI.color = BOX_BG;
            GUI.Box(new Rect(boxX, boxY, BOX_WIDTH, boxH), GUIContent.none);
            GUI.color = Color.white;

            // Draw bottom-up: index 0 is the bottom row, then upward by sort order.
            string myScene = SceneManager.GetActiveScene().name;
            float contentX = boxX + BOX_PADDING;
            for (int i = 0; i < rowCount; i++)
            {
                float rowY = boxY + boxH - BOX_PADDING - ROW_HEIGHT * (i + 1);
                DrawRow(peers[i], contentX, rowY, psm, net, cam, camFwd, myScene);
            }

            GUI.color = prevColor;
        }

        // Sorted peers (excludes self): host first (bottom row), then by ascending JoinTime.
        private List<ClientSlot> CollectAndSortPeers(SteamNetworkManager net)
        {
            string myId = net.LocalPlayerId;
            var peers = new List<ClientSlot>();
            foreach (var s in net.GetLobbyView())
            {
                if (s.PlayerId == myId) continue;
                peers.Add(s);
            }

            peers.Sort((a, b) =>
            {
                if (a.Slot == SlotTable.HOST_SLOT && b.Slot != SlotTable.HOST_SLOT) return -1;
                if (b.Slot == SlotTable.HOST_SLOT && a.Slot != SlotTable.HOST_SLOT) return  1;
                return a.JoinTime.CompareTo(b.JoinTime);
            });
            return peers;
        }

        private void DrawRow(ClientSlot peer, float x, float y,
                              PlayerSyncManager psm, SteamNetworkManager net,
                              Camera cam, Vector3 camFwd, string myScene)
        {
            Color slotColor = PlayerSyncManager.SlotColor(peer.Slot);
            string rawName = string.IsNullOrEmpty(peer.PlayerName) ? "Peer" : peer.PlayerName;
            string name = rawName.Length > MAX_NAME_CHARS
                ? rawName.Substring(0, MAX_NAME_CHARS - 1) + "…"
                : rawName;

            // Condition dot, centered in its own column ahead of the name.
            var dotRect = new Rect(x + (DOT_WIDTH - DOT_SIZE) * 0.5f,
                                   y + (ROW_HEIGHT - DOT_SIZE) * 0.5f,
                                   DOT_SIZE, DOT_SIZE);
            GUI.color = ConditionColor(psm.GetHealthFraction(peer.PlayerId));
            GUI.DrawTexture(dotRect, dotTex);
            GUI.color = Color.white;

            float nameX = x + DOT_WIDTH + COL_GAP;
            nameStyle.normal.textColor = slotColor;
            GUI.Label(new Rect(nameX, y, NAME_WIDTH, ROW_HEIGHT), name, nameStyle);

            // Ping. "--ms" dim gray when unknown.
            float ping = net.GetPingForPeer(peer.PlayerId);
            string pingText = ping <= 0f ? "--ms" : $"{ping:F0}ms";
            pingStyle.normal.textColor = ping <= 0f ? DIM_GRAY : Color.white;
            float pingX = nameX + NAME_WIDTH + COL_GAP;
            GUI.Label(new Rect(pingX, y, PING_WIDTH, ROW_HEIGHT), pingText, pingStyle);

            float arrowSlotX = pingX + PING_WIDTH + COL_GAP;
            float remainingW = (x + BOX_WIDTH - BOX_PADDING * 2f) - arrowSlotX;

            var dummy = psm.GetDummy(peer.PlayerId);
            string peerScene = psm.GetLastKnownScene(peer.PlayerId);
            bool sameScene = dummy != null && !string.IsNullOrEmpty(peerScene) && peerScene == myScene;

            if (sameScene)
            {
                Vector3 toOther = dummy.transform.position - cam.transform.position;
                toOther.y = 0f;

                if (toOther.sqrMagnitude >= 0.01f)
                {
                    float dist = toOther.magnitude;
                    toOther.Normalize();
                    float yaw = Vector3.SignedAngle(camFwd, toOther, Vector3.up);

                    // Arrow centered in its cell.
                    float arrowCx = arrowSlotX + ARROW_WIDTH * 0.5f;
                    float arrowCy = y + ROW_HEIGHT * 0.5f;
                    var arrowRect = new Rect(arrowCx - ARROW_SIZE * 0.5f, arrowCy - ARROW_SIZE * 0.5f, ARROW_SIZE, ARROW_SIZE);
                    var pivot = new Vector2(arrowCx, arrowCy);

                    var prevMatrix = GUI.matrix;
                    GUIUtility.RotateAroundPivot(yaw, pivot);
                    GUI.color = slotColor;
                    GUI.DrawTexture(arrowRect, arrowTex);
                    GUI.matrix = prevMatrix;
                    GUI.color = Color.white;

                    float distX = arrowSlotX + ARROW_WIDTH + COL_GAP;
                    GUI.Label(new Rect(distX, y, DIST_WIDTH, ROW_HEIGHT), $"{dist:F0}m", distStyle);
                }
            }
            else
            {
                // Different scene (or unknown) - replace the arrow/distance area with "(in SCENE)".
                string sceneText = !string.IsNullOrEmpty(peerScene)
                    ? $"(in {peerScene})"
                    : "(elsewhere)";
                GUI.Label(new Rect(arrowSlotX, y, remainingW, ROW_HEIGHT), sceneText, sceneStyle);
            }
        }
    }
}
