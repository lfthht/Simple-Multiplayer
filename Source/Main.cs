
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using UnityEngine;
using KSP.UI.Screens;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
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
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.SPACECENTER) return;

            if (showMenu)
            {
                windowRect = GUILayout.Window(0, windowRect, DrawMenu, "Vessel Export/Import");
            }

            if (showSettings)
            {
                windowRect = GUILayout.Window(1, windowRect, DrawSettingsMenu, "Settings");
            }
        }

        private void OnAppLauncherReady()
        {
            if (ApplicationLauncher.Instance == null) return;
            if (s_Button != null) return;

            s_Button = ApplicationLauncher.Instance.AddModApplication(
                onTrue: () => { showMenu = true; showSettings = false; }, // open
                onFalse: () => { showMenu = false; showSettings = false; }, // close
                null, null, null, null,
                ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT,
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

            if (GUILayout.Button("Export Vessel"))
            {
                StartCoroutine(vesselExporter.ExportVesselToServer());
            }

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
