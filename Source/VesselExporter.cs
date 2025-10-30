
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3


using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    public class VesselExporter
    {
        private readonly MonoBehaviour coroutineRunner;

        public VesselExporter(MonoBehaviour runner)
        {
            coroutineRunner = runner;
        }

        // ------------------------------------
        // Helpers: normalize only PRELAUNCH
        // ------------------------------------
        private static void NormalizePrelaunch(ConfigNode vNode)
        {
            if (vNode == null) return;

            string sit = vNode.GetValue("sit") ?? "";
            if (!string.Equals(sit, "PRELAUNCH", StringComparison.Ordinal)) return;
        }

        // ----------------------------
        // DELETE
        // ----------------------------
        public IEnumerator DeleteVesselFromServer(string user, string vessel)
        {
            Debug.Log($"[VesselExporter] Delete '{vessel}' for user '{user}'");

            string sanitizedVessel = string.Concat(vessel.Split(Path.GetInvalidFileNameChars()));
            string encodedVessel = UnityWebRequest.EscapeURL(sanitizedVessel);

            var www = UnityWebRequest.Delete($"{GlobalConfig.ServerUrl}/vessels/{user}/{encodedVessel}");
            yield return www.SendWebRequest();

            if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
            {
                Debug.Log($"[VesselExporter] Delete OK: '{vessel}'");
                if (coroutineRunner != null)
                    coroutineRunner.StartCoroutine(LoadVesselFilesFromServer());
            }
            else
            {
                Debug.LogError($"[VesselExporter] Delete failed: {www.error} (code {www.responseCode})");
            }
        }

        // ----------------------------
        // EXPORT (no-arg, used by Main.cs)
        // ----------------------------
        public IEnumerator ExportVesselToServer()
        {
            var activeVessel = FlightGlobals.ActiveVessel;

            if (activeVessel == null)
            {
                ScreenMessages.PostScreenMessage("No active vessel to export!", 3f, ScreenMessageStyle.UPPER_CENTER);
                yield break;
            }

            yield return SaveAndUploadProtoVessel(activeVessel, activeVessel.vesselName);
        }

        // Back-compat overload used by your Main.cs
        public IEnumerator ExportVesselToServer(Vessel v, string vesselName)
        {
            if (v == null)
            {
                ScreenMessages.PostScreenMessage("No vessel to export!", 3f, ScreenMessageStyle.UPPER_CENTER);
                yield break;
            }

            string name = string.IsNullOrWhiteSpace(vesselName) ? v.vesselName : vesselName;
            yield return SaveAndUploadProtoVessel(v, name);
        }

        private IEnumerator SaveAndUploadProtoVessel(Vessel v, string nameToUse)
        {
            string tempFile = null;
            Exception prepEx = null;

            try
            {
                var proto = v.protoVessel;
                if (proto == null) throw new Exception("Vessel has no proto snapshot!");

                string sanitized = string.Concat((nameToUse ?? "Vessel").Split(Path.GetInvalidFileNameChars()));
                tempFile = Path.Combine(Path.GetTempPath(), sanitized + ".txt");

                var node = new ConfigNode();
                proto.Save(node);

                // Only change PRELAUNCH → a sane state (keep everything else)
                NormalizePrelaunch(node);

                node.Save(tempFile);
            }
            catch (Exception ex)
            {
                prepEx = ex;
            }

            if (prepEx != null)
            {
                Debug.LogError("[VesselExporter] SaveAndUploadProtoVessel failed: " + prepEx);
                ScreenMessages.PostScreenMessage("Export failed.", 3f, ScreenMessageStyle.UPPER_CENTER);
                yield break;
            }

            yield return UploadFile(tempFile);
        }

        private IEnumerator UploadFile(string filePath)
        {
            byte[] fileData = File.ReadAllBytes(filePath);
            WWWForm form = new WWWForm();
            form.AddBinaryData("file", fileData, Path.GetFileName(filePath), "application/octet-stream");

            var www = UnityWebRequest.Post($"{GlobalConfig.ServerUrl}/upload/{GlobalConfig.userName}", form);
            yield return www.SendWebRequest();

            if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
            {
                if (coroutineRunner != null)
                    coroutineRunner.StartCoroutine(LoadVesselFilesFromServer());
            }
            else
            {
                Debug.LogError($"[VesselExporter] Upload failed: {www.error} (code {www.responseCode})");
            }
        }

        // ----------------------------
        // LIST
        // ----------------------------
        public IEnumerator LoadVesselFilesFromServer()
        {
            yield return LoadVesselFilesFromServer(onDone: null);
        }

        public IEnumerator LoadVesselFilesFromServer(Action<List<string>> onDone)
        {
            var www = UnityWebRequest.Get($"{GlobalConfig.ServerUrl}/vessels");
            yield return www.SendWebRequest();

            if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
            {
                try
                {
                    string responseText = (www.downloadHandler != null ? www.downloadHandler.text : "").Trim();
                    List<string> vesselList = ParseServerVesselList(responseText);

                    if (Main.Instance != null)
                        Main.Instance.vesselFiles = vesselList.ToArray();

                    onDone?.Invoke(vesselList);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VesselExporter] Parse vessel list failed: {ex.Message}");
                    if (Main.Instance != null) Main.Instance.vesselFiles = new string[0];
                    onDone?.Invoke(new List<string>());
                }
            }
            else
            {
                Debug.LogError("[VesselExporter] Load list failed: " + www.error);
                if (Main.Instance != null) Main.Instance.vesselFiles = new string[0];
                onDone?.Invoke(new List<string>());
            }
        }

        private List<string> ParseServerVesselList(string responseText)
        {
            var vesselList = new List<string>();
            if (string.IsNullOrWhiteSpace(responseText)) return vesselList;

            // Format: user1:v1,v2;user2:x,y
            string[] userEntries = responseText.Split(';');
            foreach (string entry in userEntries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string[] parts = entry.Split(':');
                if (parts.Length == 2)
                {
                    string user = parts[0].Trim();
                    string[] vessels = parts[1].Split(',');
                    foreach (string v in vessels)
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                            vesselList.Add($"{user}:{v.Trim()}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[VesselExporter] Malformed list entry: {entry}");
                }
            }
            return vesselList;
        }

        // ----------------------------
        // IMPORT
        // ----------------------------
        public IEnumerator ImportVesselFromServer(string user, string vessel)
        {
            string sanitizedVessel = string.Concat(vessel.Split(Path.GetInvalidFileNameChars()));
            string encodedFileName = UnityWebRequest.EscapeURL(sanitizedVessel);
            string url = $"{GlobalConfig.ServerUrl}/vessels/{user}/{encodedFileName}";

            var www = UnityWebRequest.Get(url);
            yield return www.SendWebRequest();

            if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), sanitizedVessel);
                File.WriteAllBytes(tempFile, www.downloadHandler.data);
                ImportVessel(tempFile);
            }
            else
            {
                Debug.LogError($"[VesselExporter] Import download failed: {www.error} (code {www.responseCode})");
                ScreenMessages.PostScreenMessage("Failed to download vessel.", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        public void ImportVessel(string filePath)
        {
            try
            {
                ConfigNode vesselNode = ConfigNode.Load(filePath);
                if (vesselNode == null)
                {
                    ScreenMessages.PostScreenMessage("Invalid vessel file!", 3f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }

                // Only strip PRELAUNCH; keep everything else as saved
                NormalizePrelaunch(vesselNode);

                string persistentIdString = vesselNode.GetValue("pid");
                if (string.IsNullOrEmpty(persistentIdString))
                {
                    ScreenMessages.PostScreenMessage("Vessel file is missing a persistent ID!", 3f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }

                if (Guid.TryParse(persistentIdString, out Guid persistentId))
                {
                    if (IsPersistentIdInGame(persistentId))
                    {
                        ScreenMessages.PostScreenMessage("Vessel with the same persistent ID already exists!", 3f, ScreenMessageStyle.UPPER_CENTER);
                        return;
                    }

                    ProtoVessel protoVessel = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                    protoVessel.Load(HighLogic.CurrentGame.flightState);

                    ScreenMessages.PostScreenMessage("Vessel imported successfully!", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
                else
                {
                    ScreenMessages.PostScreenMessage("Invalid persistent ID in vessel file!", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            catch (Exception ex)
            {
                ScreenMessages.PostScreenMessage($"Failed to import vessel: {ex.Message}", 3f, ScreenMessageStyle.UPPER_CENTER);
                Debug.LogError($"[VesselExporter] Import error: {ex}");
            }
        }

        private bool IsPersistentIdInGame(Guid persistentId)
        {
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel != null && vessel.id == persistentId)
                    return true;
            }
            return false;
        }
    }
}
