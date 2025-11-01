
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using KSP.UI.Screens;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class TechVote : MonoBehaviour
    {
        private static TechVote _inst;
        private static PopupDialog _waitDlg;

        // track currently open votes and their voter popups
        private static readonly HashSet<string> _seenOpen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PopupDialog> _voterDlgs = new Dictionary<string, PopupDialog>(StringComparer.Ordinal);

        private void Awake()
        {
            if (_inst != null) { Destroy(this); return; }
            _inst = this;
            DontDestroyOnLoad(gameObject);
        }

        // Call from ScenarioSync when user clicks “Research”
        public static void StartLocalVote(RDTech tech)
        {
            if (tech == null) return;
            if (_inst == null) new GameObject("TechVote").AddComponent<TechVote>();
            _inst.StartCoroutine(_inst.CoStartLocalVote(tech));
        }

        private IEnumerator CoStartLocalVote(RDTech tech)
        {
            var rnd = ResearchAndDevelopment.Instance;
            if (rnd == null) yield break;

            string techID = tech.techID;
            string title = tech.title;
            float cost = tech.scienceCost;

            // refund and re-lock during vote
            var proto = rnd.GetTechState(techID);
            if (proto != null)
            {
                rnd.AddScience(cost, TransactionReasons.None);
                proto.state = RDTech.State.Unavailable;
                try { ResearchAndDevelopment.RefreshTechTreeUI(); } catch { }
            }

            ShowWaiting(techID, title);

            // start vote
            var payload = "{\"user\":\"" + SafeUser() + "\",\"title\":\"" + EscapeJson(title) + "\",\"cost\":" + cost.ToString(CultureInfo.InvariantCulture) + "}";
            string startUrl = GlobalConfig.ServerUrl + "/vote/start/" + GlobalConfig.sharedSaveId + "/" + techID;
            yield return SendJson(startUrl, payload);

            // requester auto-votes YES so 2-player votes resolve
            yield return StartCoroutine(CoCastVote(techID, true));

            // poll status
            while (true)
            {
                yield return new WaitForSecondsRealtime(1f);
                string statusUrl = GlobalConfig.ServerUrl + "/vote/status/" + GlobalConfig.sharedSaveId + "/" + techID;
                var www = UnityWebRequest.Get(statusUrl);
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError) continue;

                string json = www.downloadHandler.text ?? string.Empty;
                if (string.IsNullOrEmpty(json)) { CloseWaiting(); yield break; }

                string norm = json.Replace(" ", "").Replace("\n", "").Replace("\r", "").ToLowerInvariant();
                bool decided = norm.Contains("\"decided\":true");
                if (!decided) continue;

                bool approved = norm.Contains("\"approved\":true");
                CloseWaiting();

                if (approved)
                    CommitUnlockAndPost(techID, cost);

                break;
            }
        }

        private void CommitUnlockAndPost(string techID, float cost)
        {
            var rnd = ResearchAndDevelopment.Instance;
            if (rnd == null) return;

            // spend points
            rnd.AddScience(-cost, TransactionReasons.None);

            // set researchable then finalize unlock so parts/UI update
            var proto = rnd.GetTechState(techID);
            if (proto != null)
            {
                proto.state = RDTech.State.Available;
                rnd.UnlockProtoTechNode(proto);
            }

            try { ResearchAndDevelopment.RefreshTechTreeUI(); } catch { }

            // send only this node
            StartCoroutine(CoPostSingleTechNode(techID));
        }



        private IEnumerator CoPostSingleTechNode(string techID)
        {
            var rnd = ResearchAndDevelopment.Instance;
            if (rnd == null) yield break;

            var tmp = new ConfigNode();
            rnd.OnSave(tmp);

            var techTree = new ConfigNode();
            foreach (var n in tmp.GetNodes("Tech"))
            {
                if (string.Equals(n.GetValue("id"), techID, StringComparison.Ordinal))
                { techTree.AddNode(n); break; }
            }
            if (techTree.values.Count == 0 && techTree.nodes.Count == 0) yield break;

            string url = GlobalConfig.ServerUrl + "/scenarios/" + GlobalConfig.sharedSaveId + "/TechTree";
            byte[] bytes = Encoding.UTF8.GetBytes(techTree.ToString());
            var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
            yield return req.SendWebRequest();
        }

        private void Start()
        {
            StartCoroutine(CoPollOpenVotes());
        }

        private IEnumerator CoPollOpenVotes()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(1f);
                // Allow Space Center, Flight (incl. map), and Tracking Station
                if (!HighLogic.LoadedSceneIsFlight &&
                    HighLogic.LoadedScene != GameScenes.SPACECENTER &&
                    HighLogic.LoadedScene != GameScenes.TRACKSTATION)
                    continue;


                string url = GlobalConfig.ServerUrl + "/vote/open/" + GlobalConfig.sharedSaveId;
                var www = UnityWebRequest.Get(url);
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError) continue;

                var me = SafeUser();

                var lines = (www.downloadHandler.text ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // compute open set
                var openNow = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;
                    openNow.Add(parts[0]);
                }

                // close voter dialogs for votes that ended
                var toDrop = new List<string>();
                foreach (var id in _seenOpen)
                    if (!openNow.Contains(id)) toDrop.Add(id);
                foreach (var id in toDrop)
                {
                    _seenOpen.Remove(id);
                    if (_voterDlgs.TryGetValue(id, out var dlg))
                    {
                        try { dlg.Dismiss(); } catch { }
                        _voterDlgs.Remove(id);
                    }
                }

                // show voter popup once per currently open vote (skip requester)
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;

                    string techID = parts[0];
                    string title = parts[1];
                    string requester = parts[2];

                    if (_seenOpen.Contains(techID)) continue;
                    if (string.Equals((requester ?? "").Trim(), me, StringComparison.Ordinal)) continue;

                    var dlg = ShowVotePopup(techID, title, requester);
                    _seenOpen.Add(techID);
                    _voterDlgs[techID] = dlg;
                }
            }
        }

        private PopupDialog ShowVotePopup(string techID, string title, string requester)
        {
            var dialog = new MultiOptionDialog(
                "SM_TechVote_" + techID,
                "Approve research?\n\n" + title + "\nRequested by: " + requester,
                "Tech Vote",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUIButton("Yes", () => StartCoroutine(CoCastVote(techID, true)),  true),
                    new DialogGUIButton("No",  () => StartCoroutine(CoCastVote(techID, false)), true),
                }
            );
            return PopupDialog.SpawnPopupDialog(dialog, false, HighLogic.UISkin);
        }

        private IEnumerator CoCastVote(string techID, bool yes)
        {
            string url = GlobalConfig.ServerUrl + "/vote/cast/" + GlobalConfig.sharedSaveId + "/" + techID;
            var payload = "{\"user\":\"" + SafeUser() + "\",\"vote\":" + (yes ? "true" : "false") + "}";
            yield return SendJson(url, payload);
        }

        // initiator cancel
        private IEnumerator CoCancelVote(string techID)
        {
            string url = GlobalConfig.ServerUrl + "/vote/cancel/" + GlobalConfig.sharedSaveId + "/" + techID;
            var payload = "{\"user\":\"" + SafeUser() + "\"}";
            yield return SendJson(url, payload);
            CloseWaiting();

            // restore to researchable so user can try again later
            var rnd = ResearchAndDevelopment.Instance;
            if (rnd != null)
            {
                var proto = rnd.GetTechState(techID);
                if (proto != null)
                {
                    proto.state = RDTech.State.Available;
                    try { ResearchAndDevelopment.RefreshTechTreeUI(); } catch { }
                }
            }
        }

        private static IEnumerator SendJson(string url, string json)
        {
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
        }

        private static string SafeUser()
        {
            try
            {
                var u = (GlobalConfig.userName ?? "").Trim();
                if (u.Length > 0) return u;
            }
            catch { }

            var id = PlayerPrefs.GetString("SM_UID", "");
            if (string.IsNullOrEmpty(id))
            {
                id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                PlayerPrefs.SetString("SM_UID", id);
                PlayerPrefs.Save();
            }
            return id;
        }

        private static string EscapeJson(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void ShowWaiting(string techID, string title)
        {
            CloseWaiting();
            var dlg = new MultiOptionDialog(
                "SM_TechVote_Wait",
                "Waiting for vote:\n\n" + title,
                "Voting",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUILabel("Please wait…"),
                    new DialogGUIButton("Cancel", () => _inst.StartCoroutine(_inst.CoCancelVote(techID)), true),
                }
            );
            _waitDlg = PopupDialog.SpawnPopupDialog(dlg, false, HighLogic.UISkin);
        }

        private static void CloseWaiting()
        {
            if (_waitDlg != null) { try { _waitDlg.Dismiss(); } catch { } _waitDlg = null; }
        }
    }
}
