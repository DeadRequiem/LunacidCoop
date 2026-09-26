using Steamworks;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LunacidCoopMod
{
    public class MPMenu : MonoBehaviour
    {
        public static MPMenu Instance;

        public bool MenuOpen { get; private set; }
        // Same config entry the transport reads, so the checkbox always reflects reality.
        public bool AutoReconnect => Plugin.AutoReconnect == null || Plugin.AutoReconnect.Value;
        public bool ShowSettings { get; private set; }
        public string StatusText { get; private set; } = "";

        private const string RAT_HUB_SCENE = "HUB_01";
        private const string RAT_PREFAB = "ded/RAT_DED1";
        private static readonly Vector3 RatPosition = new Vector3(15.5f, 0.95f, -9.65f);
        private static readonly Quaternion RatRotation = Quaternion.Euler(75f, 0f, 0f);
        private GameObject rat;
        private Player_Control_scr playerControl;
        private Menu_Control_scr menuControl;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<MPMenuGUI>();

            // Listen for overlay close so we can refresh status; Steam never tells us if an invite was sent.
            var net = SteamNetworkManager.Instance;
            if (net != null) net.OnOverlayActivated += OnOverlayActivated;
        }

        private void OnDestroy()
        {
            var net = SteamNetworkManager.Instance;
            if (net != null) net.OnOverlayActivated -= OnOverlayActivated;
        }

        private bool waitingForInviteAccept;

        // MPMenuGUI calls this when Invite Friend is clicked so the next overlay close advances status.
        public void NotifyInviteOpened()
        {
            waitingForInviteAccept = true;
        }

        private void OnOverlayActivated(bool active)
        {
            // Pair the overlay close with our own invite flag - otherwise it could fire for any overlay.
            if (!active && waitingForInviteAccept)
            {
                waitingForInviteAccept = false;
                SetStatus("INVITE OVERLAY CLOSED - WAITING FOR FRIEND TO JOIN.");
            }
        }

        private void Update()
        {
            if (SceneManager.GetActiveScene().name == RAT_HUB_SCENE && rat == null)
                SpawnRat();

            // Player_Control_scr.OnEnable() forces CursorLockMode.Locked; re-apply our own state each frame while open.
            if (MenuOpen)
            {
                if (menuControl != null)
                    Cursor.SetCursor(menuControl.Cur, new Vector2(0.2f, 0.2f), CursorMode.ForceSoftware);
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.lockState = CursorLockMode.Confined;
            }
        }

        public void ToggleMenu()
        {
            MenuOpen = !MenuOpen;
            ShowSettings = false;
            ClearStatus();

            if (MenuOpen)
            {
                if (menuControl == null)
                    menuControl = FindObjectOfType<Menu_Control_scr>();

                if (menuControl != null)
                    Cursor.SetCursor(menuControl.Cur, new Vector2(0.2f, 0.2f), CursorMode.ForceSoftware);

                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.lockState = CursorLockMode.Confined;

                playerControl = FindObjectOfType<Player_Control_scr>();
                if (playerControl != null)
                {
                    playerControl.Freeze = true;
                    playerControl.Stop();
                }
            }
            else
            {
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.lockState = CursorLockMode.Locked;

                if (playerControl != null)
                {
                    playerControl.Freeze = false;
                    playerControl = null;
                }
            }

            CoopLog.MPMenu(MenuOpen ? "Opened" : "Closed");
        }

        public void ToggleSettings() => ShowSettings = !ShowSettings;
        public void SetStatus(string text) => StatusText = text;
        public void ClearStatus() => StatusText = "";

        public void SetAutoReconnect(bool v)
        {
            SteamNetworkManager.Instance?.EnableAutoReconnect(v);
            if (SteamNetworkManager.Instance == null && Plugin.AutoReconnect != null)
                Plugin.AutoReconnect.Value = v;
        }

        private void SpawnRat()
        {
            var prefab = Resources.Load<GameObject>(RAT_PREFAB);
            if (prefab == null)
            {
                Plugin.Log.LogError($"[MPMenu] Could not load prefab: {RAT_PREFAB}");
                return;
            }

            rat = Instantiate(prefab, RatPosition, RatRotation);
            rat.name = "CoopMenuRat";

            var loot = rat.GetComponent("Loot_scr") as MonoBehaviour;
            if (loot != null) Destroy(loot);

            rat.AddComponent<MPMenuInteraction>();

            var col = rat.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(2f, 2f, 2f);
            col.center = Vector3.zero;

            CoopLog.MPMenu("Rat spawned");
        }
    }


    public class MPMenuGUI : MonoBehaviour
    {
        // --- Layout ----------
        private const float WIN_X = 30f;
        private const float WIN_W = 400f;
        private const float PAD = 18f;
        private const float INNER = WIN_W - PAD * 2f;
        private const float BTN_H = 32f;
        private const float ROW = 42f;
        private const float LINE = 22f;
        // --- Font sizes ----------
        private const int FONT_TITLE = 17;
        private const int FONT_VERSION = 11;
        private const int FONT_SECTION = 10;
        private const int FONT_BODY = 13;
        private const int FONT_DIM = 12;
        private const int FONT_STATUS = 12;
        private const int FONT_BTN = 12;
        private const int FONT_FIELD = 13;

        // --- Text strings ----------
        private const string TXT_MENU_TITLE = "DeadDreamers Co-Op";
        private const string TXT_VERSION_FMT = "MOD  V{0}";          // {0} = Plugin.ModVersion

        private const string TXT_HOSTING_STARTED = "HOSTING - USE INVITE FRIEND OR SHARE YOUR STEAM ID.";
        private const string TXT_INVITE_OPENED = "PICK A FRIEND IN THE OVERLAY TO SEND THEM AN INVITE.";
        private const string TXT_INVITE_CLOSED = "INVITE OVERLAY CLOSED - WAITING FOR FRIEND TO JOIN.";
        private const string TXT_STOP_HOSTING = "STOPPED HOSTING.";
        private const string TXT_CONNECTING = "CONNECTING...";
        private const string TXT_INVALID_ID = "INVALID STEAM ID.";
        private const string TXT_ALREADY_HOST = "YOU ARE HOSTING - STOP HOSTING BEFORE JOINING.";
        private const string TXT_STEAM_ID_COPIED = "STEAM ID COPIED.";
        private const string TXT_DISCONNECTED = "DISCONNECTED.";
        private const string TXT_CONN_LOCAL = "CONNECTING TO LOCALHOST:7777...";
        private const string TXT_HOST_LOCAL = "HOSTING - LAUNCH SECOND INSTANCE AND JOIN.";
        private const string TXT_NOT_FOUND = "STEAMNETWORKMANAGER NOT FOUND";
        private const string TXT_STEAM_NOT_READY = "(STEAM NOT READY)";
        private const string TXT_WAITING_CONN = "WAITING FOR A CONNECTION...";
        private const string TXT_WAITING_PLAYER = "WAITING FOR A PLAYER...";
        private const string TXT_SESSION_OPEN = "YOUR SESSION IS OPEN.";

        // --- Button labels ----------
        private const string BTN_HOST = "HOST GAME";
        private const string BTN_JOIN = "JOIN";
        private const string BTN_STOP_HOSTING = "STOP HOSTING";
        private const string BTN_DISCONNECT = "DISCONNECT";
        private const string BTN_SETTINGS = "SETTINGS";
        private const string BTN_BACK = "BACK";
        private const string BTN_COPY = "COPY";
        private const string BTN_INVITE = "INVITE FRIEND";
        private const string BTN_HOST_LOCAL = "HOST  (PORT 7777)";
        private const string BTN_JOIN_LOCAL = "JOIN  (LOCALHOST:7777)";

        // --- Section labels (middle-dot in strings is intentional) ------------------------
        private const string SEC_MULTIPLAYER = "MULTIPLAYER";
        private const string SEC_JOIN_BY_ID = "JOIN BY STEAM ID";
        private const string SEC_YOUR_ID = "YOUR STEAM ID";
        private const string SEC_HOSTING = "HOSTING";
        private const string SEC_CONNECTED = "CONNECTED";
        private const string SEC_SETTINGS = "SETTINGS";
        private const string SEC_LOCAL = "LOCAL TEST MODE  ·  TCP LOCALHOST:7777";

        // --- Colour palette ----------
        private const string HEX_TITLE = "#FFFFFF";
        private const string HEX_BODY = "#FFFFFF";
        private const string HEX_SUBTEXT = "#AAAAAA";
        private const string HEX_SECTION = "#CC2288";
        private const string HEX_STATUS = "#4DFFD2";
        private const string HEX_DIM = "#9E9E9E";

        private const string HEX_PANEL_BG = "#1A0A1E";
        private const string HEX_DIVIDER = "#7A1A5A";

        private const string HEX_BTN_FG = "#FFFFFF";
        private const string HEX_BTN_BG = "#2E0A28";
        private const string HEX_BTN_HOV = "#4A1040";
        private const string HEX_BTN_ACT = "#180516";

        private const string HEX_ACC_FG = "#FFFFFF";
        private const string HEX_ACC_BG = "#0A3A40";
        private const string HEX_ACC_HOV = "#0E5560";

        private const string HEX_DGR_FG = "#FFFFFF";
        private const string HEX_DGR_BG = "#8B0000";
        private const string HEX_DGR_HOV = "#C0392B";

        private const string HEX_FIELD_TEXT = "#F5A623";
        private const string HEX_FIELD_FOCUS = "#FFFFFF";
        private const string HEX_FIELD_BG = "#120820";

        // Derived colours
        private static Color COL_TITLE, COL_BODY, COL_SUBTEXT, COL_SECTION,
                             COL_STATUS, COL_DIM;
        private static Color COL_BTN_FG, COL_BTN_BG, COL_BTN_HOV, COL_BTN_ACT;
        private static Color COL_ACC_FG, COL_ACC_BG, COL_ACC_HOV;
        private static Color COL_DGR_FG, COL_DGR_BG, COL_DGR_HOV;
        private static Color COL_FIELD_TEXT, COL_FIELD_FOCUS, COL_FIELD_BG;
        private static Color COL_PANEL_BG, COL_DIVIDER;
        private static bool coloursResolved;

        private static void ResolveColours()
        {
            if (coloursResolved) return;
            COL_TITLE = Hex(HEX_TITLE);
            COL_BODY = Hex(HEX_BODY);
            COL_SUBTEXT = Hex(HEX_SUBTEXT);
            COL_SECTION = Hex(HEX_SECTION);
            COL_STATUS = Hex(HEX_STATUS);
            COL_DIM = Hex(HEX_DIM);
            COL_BTN_FG = Hex(HEX_BTN_FG);
            COL_BTN_BG = Hex(HEX_BTN_BG);
            COL_BTN_HOV = Hex(HEX_BTN_HOV);
            COL_BTN_ACT = Hex(HEX_BTN_ACT);
            COL_ACC_FG = Hex(HEX_ACC_FG);
            COL_ACC_BG = Hex(HEX_ACC_BG);
            COL_ACC_HOV = Hex(HEX_ACC_HOV);
            COL_DGR_FG = Hex(HEX_DGR_FG);
            COL_DGR_BG = Hex(HEX_DGR_BG);
            COL_DGR_HOV = Hex(HEX_DGR_HOV);
            COL_FIELD_TEXT = Hex(HEX_FIELD_TEXT);
            COL_FIELD_FOCUS = Hex(HEX_FIELD_FOCUS);
            COL_FIELD_BG = Hex(HEX_FIELD_BG);
            COL_PANEL_BG = Hex(HEX_PANEL_BG);
            COL_DIVIDER = Hex(HEX_DIVIDER);
            coloursResolved = true;
        }

        private GUIStyle stWindow, stTitle, stVersion, stSection, stBody, stDim,
                         stStatus, stBtn, stBtnAcc, stBtnDgr, stField, stDivider;
        private bool stylesReady;
        private int lastScreenH; // resolution change forces a style rebuild

        private Texture2D txPanel;  // libs/txtbox.png
        private Texture2D txBtn, txBtnH, txBtnA;
        private Texture2D txAcc, txAccH;
        private Texture2D txDgr, txDgrH;
        private Texture2D txField, txDivider;
        private Font gameFont; // game's NotoSansKR-Light

        private MPMenu menu;
        private string steamIdInput = "";

        private void Awake()
        {
            menu = GetComponent<MPMenu>();
            LoadAssets();
        }

        private void OnDestroy() => DestroyTextures();

        private void LoadAssets()
        {
            // Panel texture is embedded as <RootNamespace>.<filename>.
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("LunacidCoop.libs.txtbox.png"))
                {
                    if (stream != null)
                    {
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        txPanel = new Texture2D(64, 64, TextureFormat.RGBA32, false)
                        { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                        txPanel.LoadImage(bytes);
                        CoopLog.MPMenu("Loaded txtbox.png");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[MPMenu] txtbox.png not found as embedded resource");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[MPMenu] Could not load txtbox.png: {e.Message}");
            }

            try
            {
                gameFont = Resources.Load<Font>("fonts & materials/NotoSansKR-Light");
                if (gameFont != null)
                    CoopLog.MPMenu("Loaded NotoSansKR-Light");
                else
                    Plugin.Log.LogWarning("[MPMenu] NotoSansKR-Light not found in Resources");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[MPMenu] Could not load font: {e.Message}");
            }
        }

        private void OnGUI()
        {
            ResolveColours();
            // Resolution changes can invalidate font hinting; rebuild styles on first frame and on resolution change.
            if (!stylesReady || Screen.height != lastScreenH)
            {
                BuildStyles();
                lastScreenH = Screen.height;
            }
            if (menu.MenuOpen) DrawMenu();
        }

        private void DrawMenu()
        {
            var net = SteamNetworkManager.Instance;
            float winH = MeasureContent(net);
            float winY = 30f;

            GUI.Box(new Rect(WIN_X, winY, WIN_W, winH), GUIContent.none, stWindow);

            float y = winY + PAD;

            // Title (Escape / rat interaction dismisses the menu).
            GUI.Label(new Rect(WIN_X + PAD, y, INNER, 22), TXT_MENU_TITLE, stTitle);
            y += 24f;

            GUI.Label(new Rect(WIN_X + PAD, y, INNER, 16),
                string.Format(TXT_VERSION_FMT, Plugin.ModVersion), stVersion);
            y += 20f;

            HRule(ref y);

            if (net == null)
            {
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, 20), TXT_NOT_FOUND, stDim);
                return;
            }

            if (menu.ShowSettings)
            {
                DrawSettings(ref y);
                return;
            }

            bool local = Plugin.LocalTestMode?.Value == true;

            if (!net.IsConnected && !net.IsHost)
            {
                if (local) DrawLocal(net, ref y);
                else DrawIdle(net, ref y);
            }
            else if (net.IsHost && !net.IsConnected)
            {
                DrawHosting(net, ref y);
            }
            else
            {
                DrawConnected(net, ref y);
            }

            if (!string.IsNullOrEmpty(menu.StatusText))
            {
                HRule(ref y);
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, 36), menu.StatusText, stStatus);
                y += 38f;
            }

            HRule(ref y);
            if (Btn(BTN_SETTINGS, ref y))
                menu.ToggleSettings();
        }

        private void DrawIdle(SteamNetworkManager net, ref float y)
        {
            SectionLabel(SEC_MULTIPLAYER, ref y);

            if (Btn(BTN_HOST, ref y, accent: true))
            {
                net.StartHost();
                menu.SetStatus(TXT_HOSTING_STARTED);
            }

            HRule(ref y);
            SectionLabel(SEC_JOIN_BY_ID, ref y);

            steamIdInput = GUI.TextField(
                new Rect(WIN_X + PAD, y, INNER, BTN_H), steamIdInput, stField);
            y += ROW;

            if (Btn(BTN_JOIN, ref y, accent: true))
                TryJoin(net);

            HRule(ref y);
            SectionLabel(SEC_YOUR_ID, ref y);
            SteamIdRow(ref y);
        }

        private void DrawLocal(SteamNetworkManager net, ref float y)
        {
            SectionLabel(SEC_LOCAL, ref y);

            if (Btn(BTN_HOST_LOCAL, ref y, accent: true))
            {
                net.StartHost();
                menu.SetStatus(TXT_HOST_LOCAL);
            }

            if (Btn(BTN_JOIN_LOCAL, ref y, accent: true))
            {
                net.ConnectToLocalHost();
                menu.SetStatus(TXT_CONN_LOCAL);
            }
        }

        private void DrawHosting(SteamNetworkManager net, ref float y)
        {
            bool isLocal = Plugin.LocalTestMode?.Value == true;

            SectionLabel(SEC_HOSTING, ref y);

            if (isLocal)
            {
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, LINE), TXT_WAITING_CONN, stStatus);
                y += LINE;
            }
            else
            {
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, LINE), TXT_SESSION_OPEN, stStatus);
                y += LINE + 4f;

                SteamIdRow(ref y);

                GUI.Label(new Rect(WIN_X + PAD, y, INNER, LINE), TXT_WAITING_PLAYER, stStatus);
                y += LINE;

                // Opens the Steam invite overlay scoped to our lobby. NotifyInviteOpened arms a status update on close.
                if (Btn(BTN_INVITE, ref y, accent: true))
                {
                    net.ActivateInviteOverlay();
                    menu.SetStatus(TXT_INVITE_OPENED);
                    menu.NotifyInviteOpened();
                }
            }

            HRule(ref y);

            if (Btn(BTN_STOP_HOSTING, ref y, danger: true))
            {
                net.Disconnect();
                menu.SetStatus(TXT_STOP_HOSTING);
            }
        }

        private void DrawConnected(SteamNetworkManager net, ref float y)
        {
            SectionLabel(SEC_CONNECTED, ref y);

            string who = net.IsHost
                ? "YOU ARE HOSTING."
                : $"CONNECTED TO {(string.IsNullOrEmpty(net.ConnectedPlayerName) ? "HOST" : net.ConnectedPlayerName.ToUpper())}.";
            GUI.Label(new Rect(WIN_X + PAD, y, INNER, 22), who, stBody);
            y += 24f;

            if (net.CurrentPing > 0)
            {
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, 20), $"PING: {net.CurrentPing:F0}MS", stDim);
                y += 22f;
            }

            HRule(ref y);

            if (Btn(BTN_DISCONNECT, ref y, danger: true))
            {
                net.Disconnect();
                menu.SetStatus(TXT_DISCONNECTED);
            }
        }

        private void DrawSettings(ref float y)
        {
            SectionLabel(SEC_SETTINGS, ref y);

            bool newAR = GUI.Toggle(new Rect(WIN_X + PAD, y, INNER, 24),
                menu.AutoReconnect, "  AUTO-RECONNECT ON DROP");
            if (newAR != menu.AutoReconnect) menu.SetAutoReconnect(newAR);
            y += 28f;

            HRule(ref y);
            if (Btn(BTN_BACK, ref y))
                menu.ToggleSettings();
        }

        private void SectionLabel(string text, ref float y)
        {
            GUI.Label(new Rect(WIN_X + PAD, y, INNER, 16), text, stSection);
            y += 20f;
        }

        private bool Btn(string label, ref float y, bool accent = false, bool danger = false)
        {
            var st = danger ? stBtnDgr : (accent ? stBtnAcc : stBtn);
            bool clicked = GUI.Button(new Rect(WIN_X + PAD, y, INNER, BTN_H), label, st);
            y += ROW;
            return clicked;
        }

        private void HRule(ref float y)
        {
            GUI.Box(new Rect(WIN_X + PAD, y, INNER, 1f), GUIContent.none, stDivider);
            y += 10f;
        }

        private void SteamIdRow(ref float y)
        {
            float copyW = 66f;
            try
            {
                string id = SteamUser.GetSteamID().ToString();
                GUI.TextField(new Rect(WIN_X + PAD, y, INNER - copyW - 4f, BTN_H), id, stField);
                if (GUI.Button(new Rect(WIN_X + PAD + INNER - copyW, y, copyW, BTN_H), BTN_COPY, stBtn))
                {
                    GUIUtility.systemCopyBuffer = id;
                    menu.SetStatus(TXT_STEAM_ID_COPIED);
                }
            }
            catch
            {
                GUI.Label(new Rect(WIN_X + PAD, y, INNER, BTN_H), TXT_STEAM_NOT_READY, stDim);
            }
            y += ROW;
        }

        private void TryJoin(SteamNetworkManager net)
        {
            if (net.IsHost)
            {
                menu.SetStatus(TXT_ALREADY_HOST);
                return;
            }
            if (ulong.TryParse(steamIdInput.Trim(), out ulong id))
            {
                net.ConnectToHost(new CSteamID(id));
                menu.SetStatus(TXT_CONNECTING);
            }
            else
            {
                menu.SetStatus(TXT_INVALID_ID);
            }
        }

        private float MeasureContent(SteamNetworkManager net)
        {
            float h = PAD + 24f + 20f + 14f;

            if (net == null) { h += 20f; return h; }

            if (menu.ShowSettings)
            {
                // label + toggle + divider + back
                h += 20f + 28f + 14f + ROW;
                return h;
            }

            bool local = Plugin.LocalTestMode?.Value == true;

            if (!net.IsConnected && !net.IsHost)
            {
                if (local)
                    h += 20f + ROW + ROW;
                else
                    // section + host + hrule + section + field + join + hrule + section + id row
                    h += 20f + ROW + 14f + 20f + ROW + ROW + 14f + 20f + ROW;
            }
            else if (net.IsHost && !net.IsConnected)
            {
                if (local)
                    h += 20f + LINE + 14f + ROW;
                else
                    // section + status + pad + id row + waiting + invite + hrule + stop
                    h += 20f + LINE + 4f + ROW + LINE + ROW + 14f + ROW;
            }
            else
            {
                h += 20f + 24f;
                if (net.CurrentPing > 0) h += 22f;
                h += 14f + ROW;
            }

            if (!string.IsNullOrEmpty(menu.StatusText)) h += 14f + 38f;
            h += 14f + ROW;

            return h;
        }

        private void BuildStyles()
        {
            // Held as instance fields so Unity doesn't GC the textures.
            txBtn = Tex(COL_BTN_BG); txBtnH = Tex(COL_BTN_HOV);
            txBtnA = Tex(COL_BTN_ACT);
            txAcc = Tex(COL_ACC_BG); txAccH = Tex(COL_ACC_HOV);
            txDgr = Tex(COL_DGR_BG); txDgrH = Tex(COL_DGR_HOV);
            txField = Tex(COL_FIELD_BG);
            txDivider = Tex(COL_DIVIDER);

            // 9-slice if loaded, solid fallback otherwise.
            var panelTex = txPanel != null ? txPanel : Tex(COL_PANEL_BG);
            var panelBorder = txPanel != null ? new RectOffset(5, 5, 5, 5) : new RectOffset(2, 2, 2, 2);

            stWindow = S(GUI.skin.box, s =>
            {
                s.border = panelBorder;
                s.normal.background = panelTex;
            });

            // Explicit font + Normal avoids Unity's bold-hinting blur on default styles.
            stTitle = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_TITLE; s.fontStyle = FontStyle.Normal;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_TITLE;
            });
            stVersion = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_VERSION; s.fontStyle = FontStyle.Normal;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_SUBTEXT;
            });
            stSection = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_SECTION; s.fontStyle = FontStyle.Bold;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_SECTION;
            });
            stBody = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_BODY; s.fontStyle = FontStyle.Normal;
                s.wordWrap = true;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_BODY;
            });
            stDim = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_DIM; s.fontStyle = FontStyle.Normal;
                s.wordWrap = true;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_DIM;
            });
            stStatus = S(GUI.skin.label, s =>
            {
                s.font = gameFont; s.fontSize = FONT_STATUS; s.fontStyle = FontStyle.Normal;
                s.wordWrap = true;
                s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
                s.normal.textColor = COL_STATUS;
            });

            stBtn = S(GUI.skin.button, s =>
            {
                s.font = gameFont; s.fontSize = FONT_BTN; s.fontStyle = FontStyle.Bold;
                s.border = new RectOffset(2, 2, 2, 2);
                s.normal = new GUIStyleState { background = txBtn, textColor = COL_BTN_FG };
                s.hover = new GUIStyleState { background = txBtnH, textColor = Color.white };
                s.active = new GUIStyleState { background = txBtnA, textColor = Color.white };
            });
            stBtnAcc = S(stBtn, s =>
            {
                s.normal = new GUIStyleState { background = txAcc, textColor = COL_ACC_FG };
                s.hover = new GUIStyleState { background = txAccH, textColor = Color.white };
                s.active = new GUIStyleState { background = txBtnA, textColor = Color.white };
            });
            stBtnDgr = S(stBtn, s =>
            {
                s.normal = new GUIStyleState { background = txDgr, textColor = COL_DGR_FG };
                s.hover = new GUIStyleState { background = txDgrH, textColor = Color.white };
                s.active = new GUIStyleState { background = txBtnA, textColor = Color.white };
            });

            stField = S(GUI.skin.textField, s =>
            {
                s.font = gameFont; s.fontSize = FONT_FIELD; s.fontStyle = FontStyle.Normal;
                s.padding = new RectOffset(8, 8, 8, 8);
                s.normal = new GUIStyleState { background = txField, textColor = COL_FIELD_TEXT };
                s.focused = new GUIStyleState { background = txField, textColor = COL_FIELD_FOCUS };
            });

            stDivider = S(GUI.skin.box, s => { s.border = new RectOffset(0, 0, 0, 0); s.normal.background = txDivider; });

            stylesReady = true;
        }

        private static GUIStyle S(GUIStyle src, Action<GUIStyle> apply)
        {
            var st = new GUIStyle(src);
            apply(st);
            return st;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Color Hex(string h)
        {
            ColorUtility.TryParseHtmlString(h, out Color c);
            return c;
        }

        private void DestroyTextures()
        {
            if (txPanel != null) Destroy(txPanel);
            Destroy(txBtn); Destroy(txBtnH); Destroy(txBtnA);
            Destroy(txAcc); Destroy(txAccH);
            Destroy(txDgr); Destroy(txDgrH);
            Destroy(txField); Destroy(txDivider);
        }
    }


    public class MPMenuInteraction : MonoBehaviour
    {
        private bool playerNear;
        private GameObject player;

        private void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other)) return;
            playerNear = true;
            player = other.gameObject;
            var ctrl = other.GetComponent<Player_Control_scr>();
            if (ctrl != null) ctrl.ACT_TRIGGER = gameObject;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other)) return;
            playerNear = false;
            player = null;
            var ctrl = other.GetComponent<Player_Control_scr>();
            if (ctrl != null && ctrl.ACT_TRIGGER == gameObject)
                ctrl.ACT_TRIGGER = null;
        }

        public void ACT()
        {
            if (!playerNear || player == null) return;
            MPMenu.Instance?.ToggleMenu();
        }

        private static bool IsPlayer(Collider c) =>
            c.name == "PLAYER" ||
            c.CompareTag("Player") ||
            c.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}