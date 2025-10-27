// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT


using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]

    public class ScenarioSync : MonoBehaviour
    {
        
        private static readonly string[] scenarioFiles = {
            "SciencePoints",
            "TechTree",
            "ScienceArchives"
        };

        private void Start()
        {
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            GameEvents.onGameStateSave.Add(OnGameStateSave);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
            Debug.Log("[ScenarioSync] Initialized");
        }

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGameStateSave.Remove(OnGameStateSave);
            GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
        }

        private Coroutine periodicUploadCoroutine;

        private void OnLevelReady(GameScenes scene)
        {
            if (HighLogic.CurrentGame == null) return;
            StartCoroutine(DownloadAndInjectMergedScenario());

            if (scene == GameScenes.SPACECENTER)
            {
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

        private Coroutine periodicDownloadCoroutine;

        private void OnGameStateSave(ConfigNode _)
        {
            Debug.Log("[ScenarioSync] Game saving – syncing scenarios to server");
            StartCoroutine(UploadScenarioParts());
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
            string[] scenarioFiles = new string[]
            {
                 "SciencePoints",
                 "TechTree",
                 "ScienceArchives"
            };

            string[] content = new string[scenarioFiles.Length];

            for (int i = 0; i < scenarioFiles.Length; i++)
            {
                string url = GlobalConfig.ServerUrl + "/scenarios/" + GlobalConfig.sharedSaveId + "/" + scenarioFiles[i];
                UnityWebRequest www = UnityWebRequest.Get(url);
                yield return www.SendWebRequest();

                if (!www.isNetworkError && !www.isHttpError && www.responseCode == 200)
                {
                    content[i] = www.downloadHandler.text;
                    Debug.Log("[ScenarioSync] Downloaded " + scenarioFiles[i]);
                }
                else
                {
                    Debug.LogWarning("[ScenarioSync] Failed to download " + scenarioFiles[i] + ": " + www.error);
                    yield break;
                }
            }

            ConfigNode merged = new ConfigNode("SCENARIO");
            merged.AddValue("name", "ResearchAndDevelopment");
            merged.AddValue("scene", "7, 8, 5, 6");

            // Merge sci
            string sciValue = content[0].Trim();
            if (!sciValue.StartsWith("sci =")) sciValue = "sci = " + sciValue;
            merged.AddValue("sci", sciValue.Split('=')[1].Trim());

            // Merge Tech nodes
            ConfigNode techTree = ConfigNode.Parse(content[1]);
            foreach (ConfigNode tech in techTree.GetNodes("Tech"))
                merged.AddNode(tech);

            // Merge Science nodes
            ConfigNode scienceArchive = ConfigNode.Parse(content[2]);
            foreach (ConfigNode sciNode in scienceArchive.GetNodes("Science"))
                merged.AddNode(sciNode);

            foreach (var sc in HighLogic.CurrentGame.scenarios)
            {
                if (sc.moduleName == "ResearchAndDevelopment")
                {
                    // Load into runtime module
                    if (sc.moduleRef != null)
                    {
                        sc.moduleRef.OnLoad(merged);
                        Debug.Log("[ScenarioSync] Injected ResearchAndDevelopment scenario");
                    }

                    // Overwrite the persistent data
                    sc.Save(merged.CreateCopy());
                    Debug.Log("[ScenarioSync] Overwrote ProtoScenarioModule with merged scenario");

                    // Force-unlock techs
                    if (sc.moduleRef is ResearchAndDevelopment rnd)
                    {
                        foreach (ConfigNode techNode in techTree.GetNodes("Tech"))
                        {
                            string techID = techNode.GetValue("id");
                            if (!string.IsNullOrEmpty(techID))
                            {
                                ProtoTechNode proto = rnd.GetTechState(techID);
                                if (proto != null && proto.state == RDTech.State.Available)
                                {
                                    rnd.UnlockProtoTechNode(proto);
                                    Debug.Log("[ScenarioSync] Unlocked tech: " + techID);
                                }
                            }
                        }
                    }

                    break;
                }
            }
            // If R&D scenario wasn't found, create and inject it
            if (!HighLogic.CurrentGame.scenarios.Exists(s => s.moduleName == "ResearchAndDevelopment"))
            {
                Debug.LogWarning("[ScenarioSync] ResearchAndDevelopment scenario not found, creating new one...");

                var proto = HighLogic.CurrentGame.AddProtoScenarioModule(
                    typeof(ResearchAndDevelopment),
                    new[] { GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER, GameScenes.EDITOR });

                proto.Load(ScenarioRunner.Instance); // Make sure it’s properly attached
                proto.Save(merged.CreateCopy());     // Inject persistent structure

                if (proto.moduleRef != null)
                {
                    proto.moduleRef.OnLoad(merged);
                    Debug.Log("[ScenarioSync] Injected new ResearchAndDevelopment moduleRef");
                }
            }

        }


        private IEnumerator UploadScenarioParts()
        {
            string[] data = new string[3];

            foreach (var sc in HighLogic.CurrentGame.scenarios)
            {
                if (sc.moduleName == "ResearchAndDevelopment" && sc.moduleRef != null)
                {
                    ConfigNode node = new ConfigNode();
                    sc.moduleRef.OnSave(node);

                    // ✅ Proper line format for SciencePoints.txt
                    data[0] = "sci = " + node.GetValue("sci");

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

            for (int i = 0; i < 3; i++)
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
