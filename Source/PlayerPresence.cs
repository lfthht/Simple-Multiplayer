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
    [KSPAddon(KSPAddon.Startup.EveryScene, true)]
    public sealed class PlayerPresence : MonoBehaviour
    {
        public struct Rec { public string user, scene, color; public bool online; public double utEpoch; public double kspUt; }
        public static PlayerPresence Instance { get; private set; }
        public IReadOnlyList<Rec> Items => _items;

        // ---- Tunables (internal only) ----
        const int HTTP_TIMEOUT = 3;           // seconds per request
        const int HTTP_RETRIES = 1;           // extra tries
        const double ONLINE_TTL = 12.0;        // seconds
        const double STICKY_GRACE = 6.0;         // seconds
        const float POST_INTERVAL = 3.0f;        // base seconds
        const float FETCH_INTERVAL = 2.0f;        // base seconds
        const float JITTER = 0.35f;       // ±35%
        // ----------------------------------

        static double GameUT()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0d; }
        }

        List<Rec> _items = new List<Rec>();
        readonly Dictionary<string, Rec> _lastByUser = new Dictionary<string, Rec>();
        System.Random _rng;
        bool _resumedBurstPending;

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Keep networking and timers alive when window is unfocused.
            Application.runInBackground = true;

            _rng = new System.Random(unchecked((int)DateTime.UtcNow.Ticks));
        }

        void Start()
        {
            GlobalConfig.LoadConfig();
            StartCoroutine(Loop());
        }

        // Trigger a quick catch-up when app regains focus or unpauses.
        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) ResumeBurst();
        }
        void OnApplicationPause(bool paused)
        {
            if (!paused) ResumeBurst();
        }
        void ResumeBurst()
        {
            if (_resumedBurstPending) return;
            _resumedBurstPending = true;
            StartCoroutine(CoResumeBurst());
        }
        IEnumerator CoResumeBurst()
        {
            // Use real-time waits to ignore timeScale.
            yield return Post();
            yield return Fetch();
            yield return new WaitForSecondsRealtime(0.1f);
            _resumedBurstPending = false;
        }

        IEnumerator Loop()
        {
            while (true)
            {
                if (SessionGate.Ready)
                {
                    yield return Post();
                    yield return new WaitForSecondsRealtime(Jittered(POST_INTERVAL));
                    yield return Fetch();
                    yield return new WaitForSecondsRealtime(Jittered(FETCH_INTERVAL));
                }
                else
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
        }

        IEnumerator Post()
        {
            string baseUrl = GlobalConfig.ServerUrl;
            string user = GlobalConfig.userName; if (string.IsNullOrEmpty(user)) yield break;

            var form = new WWWForm();
            form.AddField("scene", CurrentSceneLabel());
            form.AddField("ut_epoch", NowUnix().ToString("F0", CultureInfo.InvariantCulture));
            form.AddField("ksp_ut", GameUT().ToString("F0", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(GlobalConfig.seedValue)) form.AddField("game", GlobalConfig.seedValue);
            if (!string.IsNullOrEmpty(GlobalConfig.userColorHex)) form.AddField("color", NormalizeHex(GlobalConfig.userColorHex));

            var urls = new[]
            {
                $"{baseUrl}/presence/{UnityWebRequest.EscapeURL(user)}",
                $"{baseUrl}/presence?user={UnityWebRequest.EscapeURL(user)}"
            };

            bool ok = false;
            for (int u = 0; u < urls.Length && !ok; u++)
            {
                int attempts = 0;
                while (attempts <= HTTP_RETRIES && !ok)
                {
                    using (var req = UnityWebRequest.Post(urls[u], form))
                    {
                        req.timeout = HTTP_TIMEOUT;
                        req.downloadHandler = new DownloadHandlerBuffer();
                        yield return req.SendWebRequest();
                        ok = !(req.isNetworkError || req.isHttpError) && req.responseCode >= 200 && req.responseCode < 300;
                    }
                    if (!ok) attempts++;
                }
            }
            // On failure: sticky grace keeps status online briefly.
        }

        IEnumerator Fetch()
        {
            string baseUrl = GlobalConfig.ServerUrl;
            if (string.IsNullOrEmpty(baseUrl)) yield break;

            string[] endpoints = { $"{baseUrl}/presence", $"{baseUrl}/presence/list" };

            string body = null;
            bool ok = false;

            for (int e = 0; e < endpoints.Length && !ok; e++)
            {
                int attempts = 0;
                while (attempts <= HTTP_RETRIES && !ok)
                {
                    using (var req = UnityWebRequest.Get(endpoints[e]))
                    {
                        req.timeout = HTTP_TIMEOUT;
                        req.downloadHandler = new DownloadHandlerBuffer();
                        yield return req.SendWebRequest();
                        ok = !(req.isNetworkError || req.isHttpError) && req.responseCode == 200;
                        if (ok) body = req.downloadHandler.text ?? "";
                    }
                    if (!ok) attempts++;
                }
            }

            if (!ok || string.IsNullOrEmpty(body)) yield break;

            // Expected server lines: "user=Julius,ut=...,ksp_ut=...,scene=...,color=#RRGGBB,online=1"
            var now = NowUnix();
            var current = new List<Rec>();
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
                    else if (k == "ut" || k == "ut_epoch") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out utEpoch);
                    else if (k == "ksp_ut") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out kspUt);
                }

                if (string.IsNullOrEmpty(user)) continue;

                Rec last;
                if (!_lastByUser.TryGetValue(user, out last)) last = new Rec { user = user };

                if (!string.IsNullOrEmpty(scene)) last.scene = scene;
                if (!string.IsNullOrEmpty(color)) last.color = color;
                if (kspUt > 0) last.kspUt = kspUt;
                if (utEpoch > 0) last.utEpoch = utEpoch;
                if (online) last.online = true;

                _lastByUser[user] = last;

                if (online && !"MAINMENU".Equals(last.scene, StringComparison.OrdinalIgnoreCase))
                    current.Add(new Rec { user = last.user, scene = last.scene, color = last.color, online = true, utEpoch = last.utEpoch, kspUt = last.kspUt });
            }

            // Sticky online if no fresh row this tick, based on real time
            var merged = new List<Rec>(current.Count + _lastByUser.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < current.Count; i++) { merged.Add(current[i]); seen.Add(current[i].user); }

            foreach (var kv in _lastByUser)
            {
                if (seen.Contains(kv.Key)) continue;
                var r = kv.Value;
                if ("MAINMENU".Equals(r.scene, StringComparison.OrdinalIgnoreCase)) continue;

                var age = now - r.utEpoch;
                if (age <= ONLINE_TTL + STICKY_GRACE && r.utEpoch > 0)
                    merged.Add(new Rec { user = r.user, scene = r.scene, color = r.color, online = true, utEpoch = r.utEpoch, kspUt = r.kspUt });
            }

            _items = merged;
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

        float Jittered(float baseSeconds)
        {
            var span = baseSeconds * JITTER;
            var delta = (float)(_rng.NextDouble() * 2.0 - 1.0) * span;
            var v = baseSeconds + delta;
            return v < 0.05f ? 0.05f : v;
        }
    }
}
