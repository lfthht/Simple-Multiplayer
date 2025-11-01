
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using UnityEngine;
using KSP.UI.Screens;
using System;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class Main : MonoBehaviour
    {
        public static Main Instance { get; private set; }
        public string[] vesselFiles = new string[0];

        private ApplicationLauncherButton toolbarButton;
        private bool showMenu = false;
        private bool showSettings = false;
        private Rect windowRect = new Rect(100, 100, 400, 300);
        private VesselExporter vesselExporter;
        private Vector2 scrollPosition = Vector2.zero;
        private FlagSync flagSync;
        // --- Color menu state ---
        private string _colorHexInput;    // text field backing store
        private bool _colorValid = true;  // validation flag
        private Color _parsedColor = Color.white;
        private static ApplicationLauncherButton s_Button;
        private static GUIStyle _richLabel;


        // Presence viewer
        private bool showPresence = false;
        private Rect presenceRect = new Rect(520, 100, 250, 270);
        private Vector2 presenceScroll = Vector2.zero;

        private static string AgeYMD(double epochSec)
        {
            if (epochSec <= 0) return "0y 0m 0d";
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var dt = epoch.AddSeconds(epochSec);
            var now = DateTime.UtcNow;
            if (dt > now) return "0y 0m 0d";
            var days = (int)(now - dt).TotalDays;
            int y = days / 365; days %= 365;
            int m = days / 30; int d = days % 30;
            return $"{y}y {m}m {d}d";
        }
        private static string UtYMD(double ut)
        {
            if (ut <= 0) return "Y0 M0 D0";

            if (GameSettings.KERBIN_TIME) // Kerbin: 6h day, 426-day year
            {
                const int DaySec = 6 * 60 * 60;           // 21,600
                const int YearDays = 426;
                const int YearSec = DaySec * YearDays;

                long s = (long)ut;
                int year = (int)(s / YearSec);
                int dayOfYear = (int)((s % YearSec) / DaySec);

                // 12 “months”: 11×35 + 41 days
                int[] lens = { 35, 35, 35, 35, 35, 35, 35, 35, 35, 35, 35, 41 };
                int month = 1;
                int d = dayOfYear;
                for (int i = 0; i < lens.Length; i++)
                {
                    if (d >= lens[i]) { d -= lens[i]; month++; }
                    else break;
                }
                int day = d + 1;
                return $"Y{year} M{month} D{day}";
            }
            else // Earth-style: 24h day, 365-day year (no leap)
            {
                const int DaySec = 24 * 60 * 60;          // 86,400
                const int YearDays = 365;
                const int YearSec = DaySec * YearDays;

                long s = (long)ut;
                int year = (int)(s / YearSec);
                int dayOfYear = (int)((s % YearSec) / DaySec);

                int[] lens = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
                int month = 1;
                int d = dayOfYear;
                for (int i = 0; i < lens.Length; i++)
                {
                    if (d >= lens[i]) { d -= lens[i]; month++; }
                    else break;
                }
                int day = d + 1;
                return $"Y{year} M{month} D{day}";
            }
        }
        private static string KerbinYMD(double ut)
        {
            const int SecondsPerDay = 6 * 60 * 60; // 21,600
            const int DaysPerYear = 426;

            if (ut <= 0) return "Y1 D1"; // match stock KSP UI

            long s = (long)Math.Floor(ut);
            long secondsPerYear = SecondsPerDay * (long)DaysPerYear;

            int year = (int)(s / secondsPerYear) + 1;                 // 1-based year
            int day = (int)((s / SecondsPerDay) % DaysPerYear) + 1;  // 1..426
            return $"Y{year} D{day}";
        }

        private void Awake()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnAppLauncherDestroyed);
        }
        private static string NormalizeHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s[0] == '#' ? s.ToUpperInvariant() : ("#" + s.ToUpperInvariant());
        }

        // one-pixel swatch texture for color preview
        private static Texture2D _swatchTex;
        private static void EnsureSwatchTex()
        {
            if (_swatchTex != null) return;
            _swatchTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _swatchTex.SetPixel(0, 0, Color.white);
            _swatchTex.filterMode = FilterMode.Point;
            _swatchTex.wrapMode = TextureWrapMode.Clamp;
            _swatchTex.Apply();
        }

        private void Start()
        {
            Instance = this;
            GlobalConfig.LoadConfig();
            vesselExporter = new VesselExporter(this);
            InvokeRepeating(nameof(RefreshVesselList), 0f, 5f); // Call every 5 seconds
            Debug.Log("Simple Vessel Export/Import Mod Loaded.");

            flagSync = new FlagSync(this); // Initialize FlagSync
            InvokeRepeating(nameof(SynchronizeFlags), 0f, 5f); // Synchronize flags every 5 seconds

        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnAppLauncherDestroyed);

            if (s_Button != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(s_Button);
                s_Button = null;
            }

            CancelInvoke(nameof(RefreshVesselList));
            CancelInvoke(nameof(SynchronizeFlags));
        }

        private void RemoveToolbarButton()
        {
            if (toolbarButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                toolbarButton = null;
            }
        }

        private void OnGUI()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.EDITOR) return;

            if (showMenu)
            {
                windowRect = GUILayout.Window(0, windowRect, DrawMenu, "Vessel Export/Import");
            }

            if (showSettings)
            {
                windowRect = GUILayout.Window(1, windowRect, DrawSettingsMenu, "Settings");
            }
            if (showPresence)
            {
                presenceRect = GUILayout.Window(2, presenceRect, DrawPresenceWindow, "Players Online");
            }

        }

        private void OnAppLauncherReady()
        {
            if (ApplicationLauncher.Instance == null) return;
            if (s_Button != null) return;

            s_Button = ApplicationLauncher.Instance.AddModApplication(
                onTrue: () => { showMenu = true; showSettings = false; },
                onFalse: () => { showMenu = false; showSettings = false; },
                null, null, null, null,
                ApplicationLauncher.AppScenes.SPACECENTER
              | ApplicationLauncher.AppScenes.FLIGHT
              | ApplicationLauncher.AppScenes.VAB
              | ApplicationLauncher.AppScenes.SPH,
                GameDatabase.Instance.GetTexture("SimpleMultiplayer/Textures/icon", false)
            );
        }

        private void OnAppLauncherDestroyed()
        {
            s_Button = null; // launcher rebuilt; allow recreate
        }

        private void DrawMenu(int windowID)
        {
            GUILayout.BeginVertical();
            if (GUILayout.Button("Open Player Viewer")) showPresence = true;
            if (GUILayout.Button("Open Chat")) SimpleMultiplayer.Chat.Show();
            GUILayout.Space(6);
            GUILayout.Space(6);

            bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
            GUI.enabled = inFlight;
            if (GUILayout.Button("Export Vessel"))
            {
                StartCoroutine(vesselExporter.ExportVesselToServer());
            }
            GUI.enabled = true;

            GUILayout.Space(6);


            GUILayout.Label("Available Vessels on Server:");

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(150));
            if (vesselFiles.Length > 0)
            {
                foreach (var fileWithUser in vesselFiles)
                {
                    if (string.IsNullOrWhiteSpace(fileWithUser)) continue;

                    string[] parts = fileWithUser.Split(':');
                    if (parts.Length == 2)
                    {
                        string user = parts[0].Trim();
                        string vessel = parts[1].Trim();

                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button($"{user}: {vessel}"))
                        {
                            StartCoroutine(vesselExporter.ImportVesselFromServer(user, vessel));
                        }

                        if (GUILayout.Button("Delete", GUILayout.Width(80)))
                        {
                            StartCoroutine(vesselExporter.DeleteVesselFromServer(user, vessel));
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                GUILayout.Label("No vessels found on the server.");
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("Settings"))
            {
                showSettings = true;
                showMenu = false;
            }
            if (GUILayout.Button("Close"))
            {
                showMenu = false;
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawSettingsMenu(int windowID)
        {
            GUILayout.BeginVertical();

            // Username
            GUILayout.Label("User Name:");
            GlobalConfig.userName = GUILayout.TextField(GlobalConfig.userName);

            // Server URL
            GUILayout.Label("Server Url:");
            GlobalConfig.ServerUrl = GUILayout.TextField(GlobalConfig.ServerUrl);

            // Seed
            GUILayout.Label("Seed Value:");
            GlobalConfig.seedValue = GUILayout.TextField(GlobalConfig.seedValue);

            GUILayout.Space(8f);

            // ---- Color (Hex) ----
            GUILayout.Label("User Color (Hex, e.g. #00E5FF):");
            if (_colorHexInput == null) // init from config once
                _colorHexInput = GlobalConfig.userColorHex ?? "";

            _colorHexInput = GUILayout.TextField(_colorHexInput, GUILayout.MinWidth(120));

            // Validate/preview
            string testHex = NormalizeHex(_colorHexInput);
            if (string.IsNullOrEmpty(testHex))
            {
                _colorValid = true;           // empty = "auto / not set"
                _parsedColor = Color.white;   // preview as white when empty
            }
            else
            {
                _colorValid = ColorUtility.TryParseHtmlString(testHex, out _parsedColor);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Preview:", GUILayout.Width(60));

            EnsureSwatchTex();

            // reserve a fixed rect for the swatch
            Rect r = GUILayoutUtility.GetRect(40, 20, GUILayout.Width(40), GUILayout.Height(20));

            // draw a simple outline box so the swatch has a border
            GUI.Box(r, GUIContent.none);

            // tint and draw the swatch
            Color prev = GUI.color;
            GUI.color = _colorValid ? _parsedColor : new Color(1f, 0.5f, 0.5f, 1f);
            GUI.DrawTexture(r, _swatchTex, ScaleMode.StretchToFill);
            GUI.color = prev;

            GUILayout.Space(10);
            if (GUILayout.Button("Clear"))
            {
                _colorHexInput = "";
            }
            GUILayout.EndHorizontal();


            // Preset swatches
            GUILayout.Label("Presets:");
            string[] presets = { "#a4a148", "#59a448", "#6b48a4", "#a45948" };
            GUILayout.BeginHorizontal();
            foreach (var hex in presets)
            {
                Color c; ColorUtility.TryParseHtmlString(hex, out c);
                prev = GUI.backgroundColor;
                GUI.backgroundColor = c;
                if (GUILayout.Button(GUIContent.none, GUILayout.Width(24), GUILayout.Height(20)))
                {
                    _colorHexInput = hex;
                    _parsedColor = c;
                    _colorValid = true;
                }
                GUI.backgroundColor = prev;
                GUILayout.Space(4);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);

            // Save / Close
            if (GUILayout.Button("Save Settings"))
            {
                // Persist color (empty = not set)
                var normalized = NormalizeHex(_colorHexInput);
                if (!string.IsNullOrEmpty(normalized) && !_colorValid)
                {
                    ScreenMessages.PostScreenMessage("Invalid color hex, not saved.", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
                else
                {
                    GlobalConfig.userColorHex = string.IsNullOrEmpty(normalized) ? "" : normalized;
                }

                GlobalConfig.SaveConfig();
                ScreenMessages.PostScreenMessage(
                    $"Settings saved! Username: {GlobalConfig.userName}, ServerUrl: {GlobalConfig.ServerUrl}, Seed: {GlobalConfig.seedValue}, Color: {(string.IsNullOrEmpty(GlobalConfig.userColorHex) ? "auto" : GlobalConfig.userColorHex)}",
                    3f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("Settings saved.");
            }

            if (GUILayout.Button("Close"))
            {
                showSettings = false;
                showMenu = true;
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawPresenceWindow(int id)
        {
            GUILayout.BeginVertical();
            presenceScroll = GUILayout.BeginScrollView(presenceScroll, GUILayout.Height(200));

            if (_richLabel == null)
            {
                _richLabel = new GUIStyle(GUI.skin.label) { richText = true };
            }

            var pp = PlayerPresence.Instance;
            if (pp == null || pp.Items == null || pp.Items.Count == 0)
            {
                GUILayout.Label("No players online.");
            }
            else
            {
                foreach (var r in pp.Items)
                {
                    // resolve color
                    Color c = Color.white;
                    if (!string.IsNullOrEmpty(r.color) && ColorUtility.TryParseHtmlString(r.color, out var parsed))
                        c = parsed;

                    string hex = ColorUtility.ToHtmlStringRGBA(c);
                    string userColored = $"<color=#{hex}>{r.user}</color>";

                    GUILayout.Label($"{userColored}  —  {r.scene}  —  {KerbinYMD(r.kspUt)}", _richLabel);
                }
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Close")) showPresence = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }


        private void RefreshVesselList()
        {
            StartCoroutine(vesselExporter.LoadVesselFilesFromServer());
        }

        private void SynchronizeFlags()
        {
            StartCoroutine(flagSync.SynchronizeFlags());
        }
    }
}
