// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;
using KSP;
using static GameEvents;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class PlayerPresence : MonoBehaviour
    {
        public struct Rec { public string user, scene, color; public bool online; public double utEpoch; public double kspUt; }
        public static PlayerPresence Instance { get; private set; }
        public IReadOnlyList<Rec> Items => _items;

        static double GameUT()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0d; }
        }


        List<Rec> _items = new List<Rec>();

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            GlobalConfig.LoadConfig();
            StartCoroutine(Loop());
        }

        IEnumerator Loop()
        {
            while (true)
            {
                yield return Post();
                yield return Fetch();
                yield return new WaitForSeconds(5f);
            }
        }

        IEnumerator Post()
        {
            string baseUrl = (GlobalConfig.ServerUrl ?? "http://127.0.0.1:5000").TrimEnd('/');
            string user = GlobalConfig.userName; if (string.IsNullOrEmpty(user)) yield break;

            var form = new WWWForm();
            form.AddField("scene", CurrentSceneLabel());
            form.AddField("ut_epoch", NowUnix().ToString("F0", CultureInfo.InvariantCulture));   // keeps you “online”
            form.AddField("ksp_ut", GameUT().ToString("F0", CultureInfo.InvariantCulture));     // what we display
            if (!string.IsNullOrEmpty(GlobalConfig.seedValue)) form.AddField("game", GlobalConfig.seedValue);
            if (!string.IsNullOrEmpty(GlobalConfig.userColorHex)) form.AddField("color", NormalizeHex(GlobalConfig.userColorHex));

            using (var req = UnityWebRequest.Post($"{baseUrl}/presence/{UnityWebRequest.EscapeURL(user)}", form))
            {
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError) yield break;
            }
        }

        IEnumerator Fetch()
        {
            string baseUrl = (GlobalConfig.ServerUrl ?? "http://127.0.0.1:5000").TrimEnd('/');
            using (var req = UnityWebRequest.Get($"{baseUrl}/presence"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError) yield break;

                var body = req.downloadHandler.text ?? "";
                var list = new List<Rec>();
                var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var ln in lines)
                {
                    string user = null, scene = null, color = null; bool online = false;
                    double utEpoch = 0, kspUt = 0;
                    var parts = ln.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var kv = parts[i].Split(new[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        var k = kv[0].Trim().ToLowerInvariant();
                        var v = kv[1].Trim();
                        if (k == "user") user = v;
                        else if (k == "scene") scene = v;
                        else if (k == "color") color = NormalizeHex(v);
                        else if (k == "online") online = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else if (k == "ut") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out utEpoch);   // server’s epoch
                        else if (k == "ksp_ut") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out kspUt); // new field
                    }
                    // when adding:
                    if (online && !string.IsNullOrEmpty(user) && !"MAINMENU".Equals(scene, StringComparison.OrdinalIgnoreCase))
                        list.Add(new Rec { user = user, scene = scene, color = color, online = true, utEpoch = utEpoch, kspUt = kspUt });

                }
                _items = list;
            }
        }

        static string CurrentSceneLabel()
        {
            var s = HighLogic.LoadedScene;
            if (s == GameScenes.SPACECENTER) return "KSC";
            if (s == GameScenes.FLIGHT) return "IN FLIGHT";
            if (s == GameScenes.TRACKSTATION) return "Tracking Station";
            if (s == GameScenes.EDITOR)
            {
                try { return EditorDriver.editorFacility == EditorFacility.SPH ? "SPH" : "VAB"; }
                catch { return "Editor"; }
            }
            var name = s.ToString();
            if (name.IndexOf("RESEARCH", StringComparison.OrdinalIgnoreCase) >= 0) return "Research and Development";
            if (name.IndexOf("MAINMENU", StringComparison.OrdinalIgnoreCase) >= 0) return "MAINMENU";
            return name;
        }

        static double NowUnix()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (DateTime.UtcNow - epoch).TotalSeconds;
        }

        static string NormalizeHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s[0] == '#' ? s : ("#" + s);
        }
    }
}
