// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT


using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;          // runtime fallback for module values
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    public class FlagSync
    {
        private readonly MonoBehaviour coroutineRunner;
        private readonly Dictionary<string, string> uploadedFlags = new Dictionary<string, string>();
        private readonly Dictionary<string, string> lastKnownFlagState = new Dictionary<string, string>();

        // NEW: remember files we already placed; and a success flag for last placement
        private readonly HashSet<string> _seenFlagFiles = new HashSet<string>();
        private bool _lastFlagPlacedOk = false;

        public FlagSync(MonoBehaviour runner)
        {
            coroutineRunner = runner;
            GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitch);
        }

        ~FlagSync()
        {
            GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitch);
        }

        private void OnSceneSwitch(GameEvents.FromToAction<GameScenes, GameScenes> scenes)
        {
            if (scenes.to == GameScenes.FLIGHT)
            {
                if (coroutineRunner != null && coroutineRunner is MonoBehaviour runner)
                    runner.StartCoroutine(SynchronizeFlagsOnSceneChange()); // settle-delay sync
                else
                    Debug.LogError("[FlagSync] Coroutine runner is null or invalid. Synchronization aborted.");
            }
        }

        private IEnumerator SynchronizeFlagsOnSceneChange()
        {
            yield return new WaitForSeconds(5f);
            Debug.Log("[FlagSync] Checking and synchronizing flags (scene-change)...");
            yield return DownloadFlagsFromServer();
        }

        public IEnumerator SynchronizeFlags()
        {
            Debug.Log("[FlagSync] Periodic synchronization started...");
            yield return CheckAndUploadFlags();
            yield return DownloadFlagsFromServer();
            Debug.Log("[FlagSync] Periodic synchronization completed.");
        }

        private IEnumerator CheckAndUploadFlags()
        {
            foreach (var flag in FlightGlobals.Vessels.FindAll(v => v.vesselType == VesselType.Flag))
            {
                if (flag == null || flag.protoVessel == null) continue;

                string persistentId = flag.protoVessel.persistentId.ToString();
                if (string.IsNullOrEmpty(persistentId) || persistentId == "0")
                {
                    Debug.LogWarning($"[FlagSync] Flag {flag.vesselName} has an invalid or missing PID. Skipping.");
                    continue;
                }

                // skip while runtime name is still the default "Flag"
                string runtimeName = flag.vesselName;
                if (string.IsNullOrWhiteSpace(runtimeName) ||
                    string.Equals(runtimeName.Trim(), "Flag", StringComparison.OrdinalIgnoreCase))
                {
                    // freshly planted / not yet named -> don't upload
                    continue;
                }
                // Skip re-uploading flags that already have a different owner in the proto
                var protoOwner = GetModuleValueFromProto(flag, "FlagSite", "placedBy");
                if (!string.IsNullOrEmpty(protoOwner) &&
                    !string.Equals(protoOwner, GlobalConfig.userName, StringComparison.Ordinal))
                {
                    continue; // foreign flag -> don't upload
                }


                string flagData = SerializeFlag(flag);

                if (!lastKnownFlagState.ContainsKey(persistentId) || lastKnownFlagState[persistentId] != flagData)
                {
                    yield return UploadFlag(flag, persistentId, flagData);
                    lastKnownFlagState[persistentId] = flagData;
                }
            }
        }

        private IEnumerator UploadFlag(Vessel flag, string persistentId, string flagData)
        {
            Debug.Log($"[FlagSync] Uploading flag: {flag.vesselName}");
            byte[] flagBytes = System.Text.Encoding.UTF8.GetBytes(flagData);
            WWWForm form = new WWWForm();
            form.AddBinaryData("file", flagBytes, $"{persistentId}.txt", "text/plain");

            UnityWebRequest www = UnityWebRequest.Post($"{GlobalConfig.ServerUrl}/flags/{GlobalConfig.userName}", form);
            yield return www.SendWebRequest();

            if (www.responseCode == 200)
            {
                uploadedFlags[persistentId] = flagData;
                Debug.Log($"[FlagSync] Successfully uploaded or updated flag: {flag.vesselName} (PID: {persistentId})");
            }
            else
            {
                Debug.LogError($"[FlagSync] Failed to upload or update flag: {flag.vesselName}. Error: {www.error}");
            }
        }

        private IEnumerator DownloadFlagsFromServer()
        {
            Debug.Log("[FlagSync] Fetching flags from server.");
            UnityWebRequest www = UnityWebRequest.Get($"{GlobalConfig.ServerUrl}/flags");
            yield return www.SendWebRequest();

            if (www.responseCode == 200)
            {
                // "user/filename;user2/filename2;..."
                string[] serverFlags = www.downloadHandler.text.Trim().Split(';');
                foreach (var flagEntry in serverFlags)
                {
                    if (string.IsNullOrWhiteSpace(flagEntry)) continue;

                    string user = null, file = null;
                    int slash = flagEntry.IndexOf('/');
                    if (slash > 0)
                    {
                        user = flagEntry.Substring(0, slash).Trim();
                        file = flagEntry.Substring(slash + 1).Trim();
                    }
                    else
                    {
                        var partsFallback = flagEntry.Split(':');
                        if (partsFallback.Length == 2) { user = partsFallback[0].Trim(); file = partsFallback[1].Trim(); }
                    }

                    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(file)) { Debug.LogWarning("[FlagSync] Malformed flag entry: " + flagEntry); continue; }
                    if (!file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    // hard skip: never download my own flags (they already live in my persistent.sfs)
                    if (string.Equals(user, GlobalConfig.userName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Avoid re-downloading our own flags if filename base matches a known local PID
                    string pidCandidate = Path.GetFileNameWithoutExtension(file);

                    // ⬇ add this block
                    ulong pidCheck;
                    if (!string.IsNullOrEmpty(pidCandidate) && ulong.TryParse(pidCandidate, out pidCheck))
                    {
                        // If a flag with this PID is already spawned (from persistent.sfs), skip downloading
                        if (FlightGlobals.Vessels.Exists(v =>
                            v != null && v.vesselType == VesselType.Flag &&
                            v.protoVessel != null && v.protoVessel.persistentId == pidCheck))
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(pidCandidate) && uploadedFlags.ContainsKey(pidCandidate)) continue;


                    // Also skip files we already placed successfully
                    string key = user + "/" + file;
                    if (_seenFlagFiles.Contains(key)) continue;

                    string url = $"{GlobalConfig.ServerUrl}/flags/{UnityWebRequest.EscapeURL(user)}/{UnityWebRequest.EscapeURL(file)}";
                    UnityWebRequest flagDownload = UnityWebRequest.Get(url);
                    yield return flagDownload.SendWebRequest();

                    if (flagDownload.responseCode == 200)
                    {
                        try
                        {
                            string flagData = flagDownload.downloadHandler.text;
                            _lastFlagPlacedOk = false;
                            DeserializeAndPlaceFlag(flagData);

                            if (_lastFlagPlacedOk)
                            {
                                _seenFlagFiles.Add(key);
                                Debug.Log($"[FlagSync] Downloaded and placed flag file '{file}' from user '{user}'");
                            }
                            else
                            {
                                Debug.LogWarning($"[FlagSync] Downloaded but did not place flag file '{file}' from user '{user}' (skipped/invalid).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[FlagSync] Failed to process flag file '{file}' from '{user}'. Error: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[FlagSync] Failed to download flag '{file}' for '{user}'. Error: {flagDownload.error}");
                    }
                }
            }
            else
            {
                Debug.LogError("[FlagSync] Failed to fetch flag list from server.");
            }
        }

        private string SerializeFlag(Vessel flag)
        {
            // Snapshot from the live flag (body only; no header name)
            var vesselBody = new ConfigNode();
            flag.protoVessel.Save(vesselBody);

            // --- Vessel name ---
            string liveName = !string.IsNullOrEmpty(flag?.vesselName) ? flag.vesselName : "Flag";
            if (vesselBody.HasValue("name")) vesselBody.SetValue("name", liveName, true);
            else vesselBody.AddValue("name", liveName);

            // --- Flags are landed vessels (keep state consistent, but do not overwrite pose) ---
            vesselBody.SetValue("type", "Flag", true);
            vesselBody.SetValue("sit", "LANDED", true);
            vesselBody.SetValue("landed", "True", true);
            vesselBody.SetValue("splashed", "False", true);

            // IMPORTANT: Do not overwrite lat/lon/alt/nrm/rot here.
            // flag.protoVessel.Save(vesselBody) already wrote the correct pose. Overwriting with
            // transform-based values can rotate the flag incorrectly on other clients.

            // Do NOT store root-level PlaqueText (stock stores plaque inside FlagSite) (stock stores plaque inside FlagSite)
            if (vesselBody.HasValue("PlaqueText")) vesselBody.RemoveValue("PlaqueText");

            // Live values to store in the module (use your existing getters)
            string plaqueText = GetPlaqueText(flag) ?? string.Empty;
            string placedBy = GetPlacedBy(flag) ?? string.Empty;

            // --- FlagSite module normalization: PlaqueText (capital P) + placedBy + state=Placed ---
            ConfigNode[] partNodes = vesselBody.GetNodes("PART");
            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode[] modNodes = partNodes[i].GetNodes("MODULE");
                for (int j = 0; j < modNodes.Length; j++)
                {
                    ConfigNode m = modNodes[j];
                    string modName = m.GetValue("name");
                    if (!string.Equals(modName, "FlagSite", StringComparison.Ordinal)) continue;

                    // Remove lowercase variant to avoid duplication
                    if (m.HasValue("plaqueText")) m.RemoveValue("plaqueText");

                    // Canonical KSP field (matches persistent.sfs)
                    if (m.HasValue("PlaqueText")) m.SetValue("PlaqueText", plaqueText, true);
                    else m.AddValue("PlaqueText", plaqueText);

                    // ✅ TINY FIX: do not overwrite an existing owner
                    var existingPlacedBy = m.HasValue("placedBy") ? m.GetValue("placedBy") : null;
                    if (string.IsNullOrWhiteSpace(existingPlacedBy))
                    {
                        if (m.HasValue("placedBy")) m.SetValue("placedBy", placedBy, true);
                        else m.AddValue("placedBy", placedBy);
                    }
                    // else: keep existingPlacedBy untouched

                    // Stock flags carry "state = Placed"
                    if (m.HasValue("state")) m.SetValue("state", "Placed", true);
                    else m.AddValue("state", "Placed");
                }
            }


            // ---- Write with a VESSEL header like stock ----
            string bodyText = vesselBody.ToString();
            string finalText = "VESSEL\n" + bodyText; // becomes: VESSEL { ... }
            return finalText;
        }

        // Robust parse + place; sets _lastFlagPlacedOk only on success
        private void DeserializeAndPlaceFlag(string flagData)
        {
            _lastFlagPlacedOk = false;

            try
            {
                if (string.IsNullOrEmpty(flagData))
                {
                    Debug.LogError("[FlagSync] Empty flag data.");
                    return;
                }

                // Try parsing as-is; if that fails, wrap with a VESSEL header and parse again.
                ConfigNode parsed = ConfigNode.Parse(flagData);
                if (parsed == null)
                {
                    string wrapped = "VESSEL" + flagData;
                    parsed = ConfigNode.Parse(wrapped);
                    if (parsed == null)
                    {
                        Debug.LogError("[FlagSync] Failed to parse flag data (both direct and wrapped).");
                        return;
                    }
                }

                // Normalize: ensure we operate on the actual VESSEL node
                ConfigNode vesselNode = parsed;
                if (string.IsNullOrEmpty(vesselNode.name) || !string.Equals(vesselNode.name, "VESSEL", StringComparison.OrdinalIgnoreCase))
                {
                    if (parsed.HasNode("VESSEL"))
                        vesselNode = parsed.GetNode("VESSEL");
                    else
                        vesselNode.name = "VESSEL"; // final fallback; operates in-place
                }

                // Must have at least one PART (the flag part). If not, skip instead of throwing.
                var parts = vesselNode.GetNodes("PART");
                if (parts == null || parts.Length == 0)
                {
                    Debug.LogWarning("[FlagSync] Skipping flag: downloaded vessel has no PART nodes after validity fix. vesselNodeName=" + vesselNode.name + ", hasVesselChild=" + parsed.HasNode("VESSEL"));
                    return;
                }

                // Spawn
                string pidValue = vesselNode.GetValue("pid");
                if (!string.IsNullOrEmpty(pidValue) && ulong.TryParse(pidValue, out ulong pid))
                {
                    //Skip if a flag with this persistentId is already in the game
                    if (FlightGlobals.Vessels.Exists(v => v != null && v.protoVessel != null && v.protoVessel.persistentId == pid))
                    {
                        Debug.Log($"[FlagSync] Skipping flag with duplicate PID {pid} (already present).");
                        return;
                    }
                }
                ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                pv.Load(HighLogic.CurrentGame.flightState);

                _lastFlagPlacedOk = true;
                Debug.Log("[FlagSync] Flag placed successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[FlagSync] Exception while deserializing/placing flag: " + ex);
            }
        }

        private static string GetPlaqueText(Vessel flag)
        {
            var v = GetModuleValueFromRuntime(flag, "FlagSite", new[] { "PlaqueText" });
            return v ?? string.Empty;
        }

        private static string GetPlacedBy(Vessel flag)
        {
            //var v = GetModuleValueFromRuntime(flag, "FlagSite", new[] {"placedBy" });
            //return v ?? string.Empty;
            return GlobalConfig.userName ?? string.Empty;
        }
        private static string GetModuleValueFromProto(Vessel flag, string moduleName, string key)
        {
            try
            {
                var pv = flag != null ? flag.protoVessel : null;
                if (pv == null || pv.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return null;

                var part = pv.protoPartSnapshots[0];
                if (part == null || part.modules == null) return null;

                foreach (var pm in part.modules)
                {
                    if (!string.Equals(pm.moduleName, moduleName, StringComparison.Ordinal)) continue;
                    var node = pm.moduleValues;
                    if (node == null) continue;

                    if (node.HasValue(key)) return node.GetValue(key);
                }
            }
            catch { }
            return null;
        }
        private static string GetModuleValueFromRuntime(Vessel flag, string moduleName, string[] keys)
        {
            try
            {
                if (flag == null || flag.Parts == null || flag.Parts.Count == 0) return null;
                Part part = flag.Parts[0];
                if (part == null || part.Modules == null) return null;

                foreach (PartModule module in part.Modules)
                {
                    // Compare by module name to avoid hard dependency on the FlagSite type
                    if (!string.Equals(module.moduleName, moduleName, StringComparison.Ordinal)) continue;

                    foreach (string key in keys)
                    {
                        // a) BaseFieldList iteration (KSP 1.x compatible)
                        try
                        {
                            if (module.Fields != null)
                            {
                                foreach (BaseField bf in module.Fields)
                                {
                                    if (bf == null) continue;
                                    if (string.Equals(bf.name, key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        object val = bf.GetValue(module);
                                        if (val != null) return val.ToString();
                                    }
                                }
                            }
                        }
                        catch { }

                        // b) Reflection: private/public field
                        try
                        {
                            var fi = module.GetType().GetField(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fi != null)
                            {
                                object val = fi.GetValue(module);
                                if (val != null) return val.ToString();
                            }
                        }
                        catch { }

                        // c) Reflection: property
                        try
                        {
                            var pi = module.GetType().GetProperty(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (pi != null && pi.CanRead)
                            {
                                object val = pi.GetValue(module, null);
                                if (val != null) return val.ToString();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
