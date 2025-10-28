// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class ScenarioSync : MonoBehaviour
    {
        private Coroutine periodicUploadCoroutine;
        private Coroutine periodicDownloadCoroutine;

        private static readonly string[] scenarioFiles =
        {
            "SciencePoints",
            "TechTree",
            "ScienceArchives"
        };

        private void Start()
        {
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved); // keep only this one
            Debug.Log("[ScenarioSync] Initialized");
        }

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
        }

        private void OnLevelReady(GameScenes scene)
        {
            if (HighLogic.CurrentGame == null) return;

            if (scene == GameScenes.SPACECENTER)
            {
                // immediate refresh only at KSC
                StartCoroutine(DownloadAndInjectMergedScenario());

                if (periodicUploadCoroutine == null)
                {
                    periodicUploadCoroutine = StartCoroutine(PeriodicUploadLoop());
                    Debug.Log("[ScenarioSync] Started periodic upload in Space Center");
                }

                if (periodicDownloadCoroutine == null)
                {
                    periodicDownloadCoroutine = StartCoroutine(PeriodicDownloadLoop());
                    Debug.Log("[ScenarioSync] Started periodic download in Space Center");
                }
            }
            else
            {
                if (periodicUploadCoroutine != null)
                {
                    StopCoroutine(periodicUploadCoroutine);
                    periodicUploadCoroutine = null;
                    Debug.Log("[ScenarioSync] Stopped periodic upload (left Space Center)");
                }

                if (periodicDownloadCoroutine != null)
                {
                    StopCoroutine(periodicDownloadCoroutine);
                    periodicDownloadCoroutine = null;
                    Debug.Log("[ScenarioSync] Stopped periodic download (left Space Center)");
                }
            }
        }

        private void OnGameStateSaved(Game game)
        {
            StartCoroutine(UploadScenarioParts());
        }

        private IEnumerator PeriodicUploadLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);
                if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
                {
                    Debug.Log("[ScenarioSync] Periodic sync from Space Center");
                    StartCoroutine(UploadScenarioParts());
                }
            }
        }

        private IEnumerator PeriodicDownloadLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);
                if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
                {
                    Debug.Log("[ScenarioSync] Periodic download from Space Center");
                    StartCoroutine(DownloadAndInjectMergedScenario());
                }
            }
        }

        private IEnumerator DownloadAndInjectMergedScenario()
        {
            string[] requestOrder = { "SciencePoints", "TechTree", "ScienceArchives" };
            string[] content = new string[requestOrder.Length];

            for (int i = 0; i < requestOrder.Length; i++)
            {
                string url = GlobalConfig.ServerUrl + "/scenarios/" + GlobalConfig.sharedSaveId + "/" + requestOrder[i];
                UnityWebRequest www = UnityWebRequest.Get(url);
                yield return www.SendWebRequest();

                if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
                {
                    content[i] = www.downloadHandler.text;
                    Debug.Log("[ScenarioSync] Downloaded " + requestOrder[i]);
                }
                else
                {
                    Debug.LogWarning("[ScenarioSync] Failed to download " + requestOrder[i] + ": " + www.error);
                    yield break;
                }
            }

            // Build merged ResearchAndDevelopment scenario
            ConfigNode merged = new ConfigNode("SCENARIO");
            merged.AddValue("name", "ResearchAndDevelopment");
            merged.AddValue("scene", "7, 8, 5, 6");

            // --- science: write to node AND adjust in-memory immediately ---
            string sciLine = (content[0] ?? "").Trim(); // "sci = <value>" or just "<value>"
            if (!string.IsNullOrEmpty(sciLine))
            {
                string raw = sciLine.Contains("=") ? sciLine.Split('=')[1].Trim() : sciLine;
                merged.AddValue("sci", raw);

                float targetSci;
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out targetSci))
                {
                    var rnd = ResearchAndDevelopment.Instance;
                    if (rnd != null)
                    {
                        float current = rnd.Science;
                        float delta = targetSci - current;
                        if (Mathf.Abs(delta) > 0.001f)
                        {
                            rnd.AddScience(delta, TransactionReasons.None);
                            Debug.Log("[ScenarioSync] Adjusted in-memory science by " + delta.ToString("0.###", CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            // TechTree (guard)
            ConfigNode techTree = null;
            if (!string.IsNullOrWhiteSpace(content[1]))
            {
                techTree = ConfigNode.Parse(content[1]);
                foreach (ConfigNode tech in techTree.GetNodes("Tech"))
                    merged.AddNode(tech);
            }

            // ScienceArchives (guard + dedupe)
            ConfigNode scienceArchive = null;
            if (!string.IsNullOrWhiteSpace(content[2]))
            {
                scienceArchive = ConfigNode.Parse(content[2]);

                var addedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ConfigNode sciNode in scienceArchive.GetNodes("Science"))
                {
                    string id = sciNode.GetValue("id");
                    if (string.IsNullOrEmpty(id)) continue;

                    if (!addedIds.Add(id)) continue; // skip duplicates in payload
                    if (ResearchAndDevelopment.GetSubjectByID(id) != null) continue; // skip if exists locally

                    merged.AddNode(sciNode);
                }
            }

            // Inject into current game
            foreach (var sc in HighLogic.CurrentGame.scenarios)
            {
                if (sc.moduleName == "ResearchAndDevelopment")
                {
                    if (sc.moduleRef != null)
                    {
                        sc.moduleRef.OnLoad(merged);
                        Debug.Log("[ScenarioSync] Injected ResearchAndDevelopment scenario");
                    }

                    sc.Save(merged.CreateCopy());
                    Debug.Log("[ScenarioSync] Overwrote ProtoScenarioModule with merged scenario");

                    // Unlock techs if needed
                    if (techTree != null && sc.moduleRef is ResearchAndDevelopment rnd)
                    {
                        foreach (ConfigNode techNode in techTree.GetNodes("Tech"))
                        {
                            string techID = techNode.GetValue("id");
                            if (string.IsNullOrEmpty(techID)) continue;

                            ProtoTechNode proto = rnd.GetTechState(techID);
                            if (proto != null && proto.state == RDTech.State.Available)
                            {
                                rnd.UnlockProtoTechNode(proto);
                                Debug.Log("[ScenarioSync] Unlocked tech: " + techID);
                            }
                        }
                    }

                    yield break;
                }
            }

            // If R&D scenario wasn't found, create and inject it
            Debug.LogWarning("[ScenarioSync] ResearchAndDevelopment scenario not found, creating new one.");
            var protoScenario = HighLogic.CurrentGame.AddProtoScenarioModule(
                typeof(ResearchAndDevelopment),
                new[] { GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER, GameScenes.EDITOR });

            protoScenario.Load(ScenarioRunner.Instance);
            protoScenario.Save(merged.CreateCopy());

            if (protoScenario.moduleRef != null)
            {
                protoScenario.moduleRef.OnLoad(merged);
                Debug.Log("[ScenarioSync] Injected new ResearchAndDevelopment moduleRef");
            }
        }

        private IEnumerator UploadScenarioParts()
        {
            // Only upload TechTree and ScienceArchives
            string[] data = new string[3]; // [0]=Sci (unused), [1]=TechTree, [2]=ScienceArchives

            foreach (var sc in HighLogic.CurrentGame.scenarios)
            {
                if (sc.moduleName == "ResearchAndDevelopment" && sc.moduleRef != null)
                {
                    ConfigNode node = new ConfigNode();
                    sc.moduleRef.OnSave(node);

                    // no SciencePoints upload

                    ConfigNode techTree = new ConfigNode();
                    foreach (ConfigNode n in node.GetNodes("Tech"))
                        techTree.AddNode(n);
                    data[1] = techTree.ToString();

                    ConfigNode science = new ConfigNode();
                    foreach (ConfigNode n in node.GetNodes("Science"))
                        science.AddNode(n);
                    data[2] = science.ToString();
                }
            }

            for (int i = 1; i < 3; i++)
            {
                string url = GlobalConfig.ServerUrl + "/scenarios/" + GlobalConfig.sharedSaveId + "/" + scenarioFiles[i];
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data[i] ?? "");
                UnityWebRequest www = new UnityWebRequest(url, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(bytes),
                    downloadHandler = new DownloadHandlerBuffer()
                };
                www.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                yield return www.SendWebRequest();

                if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
                    Debug.Log("[ScenarioSync] Uploaded " + scenarioFiles[i]);
                else
                    Debug.LogWarning("[ScenarioSync] Upload failed for " + scenarioFiles[i] + ": " + www.error);
            }
        }
    }
}
