// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT

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


        private void Start()
        {
            Instance = this;
            GlobalConfig.LoadConfig();
            vesselExporter = new VesselExporter(this);
            AddToolbarButton();
            InvokeRepeating(nameof(RefreshVesselList), 0f, 5f); // Call every 5 seconds
            Debug.Log("Simple Vessel Export/Import Mod Loaded.");

            flagSync = new FlagSync(this); // Initialize FlagSync
            InvokeRepeating(nameof(SynchronizeFlags), 0f, 5f); // Synchronize flags every 5 seconds

        }

        private void OnDestroy()
        {
            RemoveToolbarButton();
            CancelInvoke(nameof(RefreshVesselList)); // Stop refreshing when destroyed

            CancelInvoke(nameof(SynchronizeFlags)); // Stop flag synchronization
        }

        private void AddToolbarButton()
        {
            if (ApplicationLauncher.Instance != null && toolbarButton == null)
            {
                toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    () => showMenu = !showMenu,
                    () => showMenu = !showMenu,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    GameDatabase.Instance.GetTexture("SimpleMultiplayer/Textures/icon", false)
                );
            }
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
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;

            if (showMenu)
            {
                windowRect = GUILayout.Window(0, windowRect, DrawMenu, "Vessel Export/Import");
            }

            if (showSettings)
            {
                windowRect = GUILayout.Window(1, windowRect, DrawSettingsMenu, "Settings");
            }
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

            GUILayout.Label("User Name:");
            GlobalConfig.userName = GUILayout.TextField(GlobalConfig.userName);

            GUILayout.Label("Server Url:");
            GlobalConfig.ServerUrl = GUILayout.TextField(GlobalConfig.ServerUrl);

            GUILayout.Label("Seed Value:");
            GlobalConfig.seedValue = GUILayout.TextField(GlobalConfig.seedValue);

            if (GUILayout.Button("Save Settings"))
            {
                GlobalConfig.SaveConfig();
                ScreenMessages.PostScreenMessage($"Settings saved! Username: {GlobalConfig.userName}, ServerUrl: {GlobalConfig.ServerUrl}, Seed: {GlobalConfig.seedValue}", 3f, ScreenMessageStyle.UPPER_CENTER);
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
