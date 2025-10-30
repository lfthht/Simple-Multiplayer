// File: GameData/SimpleMultiplayer/GlobalConfig.cs
using System.IO;
using UnityEngine;
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT

namespace SimpleMultiplayer
{
    public static class GlobalConfig
    {
        public static string ServerUrl = "http://localhost:5000"; // Default Server URL
        public static string seedValue = "1234";                   // Default seed value
        public const string ConfigFilePath = "GameData/SimpleMultiplayer/settings.cfg";
        public static string userName = "default";                 // Default username
        public static string userColorHex = ""; // e.g. "#00E5FF"

        // NEW: all clients use the same shared save id for server calls
        public static string sharedSaveId = "default";

        public static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    ConfigNode configNode = ConfigNode.Load(ConfigFilePath);

                    if (configNode.HasValue("UserName"))
                        userName = configNode.GetValue("UserName");

                    if (configNode.HasValue("ServerUrl"))
                        ServerUrl = System.Uri.UnescapeDataString(configNode.GetValue("ServerUrl"));

                    if (configNode.HasValue("SeedValue"))
                        seedValue = configNode.GetValue("SeedValue");

                    // NEW (optional key; defaults to "default" if missing)
                    if (configNode.HasValue("SharedSaveId"))
                        sharedSaveId = (configNode.GetValue("SharedSaveId") ?? "default").Trim();
                    if (configNode.HasValue("UserColorHex"))
                        userColorHex = (configNode.GetValue("UserColorHex") ?? "").Trim();
                }
                else
                {
                    SaveConfig(); // create with defaults
                }

                Debug.Log($"[SimpleMP] Config: user='{userName}', url='{ServerUrl}', sharedSaveId='{sharedSaveId}', seed='{seedValue}'");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SimpleMP] Failed to load config: {ex.Message}");
            }
        }

        public static void SaveConfig()
        {
            try
            {
                ConfigNode configNode = new ConfigNode();
                configNode.AddValue("UserName", userName);
                configNode.AddValue("ServerUrl", System.Uri.EscapeDataString(ServerUrl));
                configNode.AddValue("SeedValue", seedValue);
                // NEW: persist sharedSaveId
                configNode.AddValue("SharedSaveId", sharedSaveId);
                configNode.Save(ConfigFilePath);
                configNode.AddValue("UserColorHex", userColorHex ?? "");
                Debug.Log("[SimpleMP] Config saved.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SimpleMP] Failed to save config: {ex.Message}");
            }
        }
    }
}
