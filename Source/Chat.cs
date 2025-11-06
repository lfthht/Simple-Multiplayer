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

namespace SimpleMultiplayer
{
    /// <summary>
    /// Simple global chat per save:
    ///  - Toggle window: F8 (no toolbar icon).
    ///  - Polls history every 2 s.
    ///  - Sends small messages (single line).
    ///  - Server stores lines as: ISO8601Z|base64(user)|base64(msg)
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public sealed class Chat : MonoBehaviour
    {
        private struct ChatMsg
        {
            public DateTime Utc;
            public string User;
            public string Text;
        }

        public static Chat Instance { get; private set; }

        private readonly List<ChatMsg> _msgs = new List<ChatMsg>(256);
        private string _lastRaw = "";
        private bool _running;
        private float _nextPollAt;
        private const float PollSeconds = 2f;

        private bool _show;
        private Rect _rect = new Rect(90, 90, 560, 420);
        private Vector2 _scroll = Vector2.zero;
        private string _input = "";
        private string _status = "";
        private bool _busyPost;

        // Thin-line UI
        private static Texture2D _lineTex;
        private static GUIStyle _msgStyle;

        private static void EnsureLineTex()
        {
            if (_lineTex != null) return;
            _lineTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _lineTex.SetPixel(0, 0, Color.white);
            _lineTex.wrapMode = TextureWrapMode.Clamp;
            _lineTex.filterMode = FilterMode.Point;
            _lineTex.Apply(false, true);
        }

        private static void HLine(float alpha = 0.22f)
        {
            EnsureLineTex();
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            r.height = 1f;
            GUI.DrawTexture(r, _lineTex);
            GUI.color = prev;
        }

        private string BaseUrl => (GlobalConfig.ServerUrl ?? "http://localhost:5000").TrimEnd('/');
        private string SaveId => string.IsNullOrEmpty(GlobalConfig.sharedSaveId) ? "default" : GlobalConfig.sharedSaveId;
        private string User => string.IsNullOrEmpty(GlobalConfig.userName) ? "anon" : GlobalConfig.userName;

        private string GetUrl() => $"{BaseUrl}/chat/{Escape(SaveId)}";
        private static string Escape(string s) => UnityWebRequest.EscapeURL(s ?? "");

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            _running = true;
            _nextPollAt = Time.realtimeSinceStartup;

            if (_msgStyle == null)
            {
                // Copy from skin label. Do NOT touch alignment to avoid TextAnchor dependency.
                var baseLabel = HighLogic.Skin.label;
                _msgStyle = new GUIStyle(baseLabel)
                {
                    wordWrap = false,
                    richText = false,
                    padding = new RectOffset(4, 4, 1, 1),
                    fontSize = baseLabel.fontSize
                };
            }
        }

        private void Update()
        {
            if (!SessionGate.Ready) return;
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _show = !_show;
                if (_show) StartCoroutine(CoFetch());
            }

            if (!_running) return;

            if (Time.realtimeSinceStartup >= _nextPollAt)
            {
                _nextPollAt = Time.realtimeSinceStartup + PollSeconds;
                StartCoroutine(CoFetch());
            }
        }

        public static void Show()
        {
            if (Instance == null) return;
            Instance._show = true;
            Instance.StartCoroutine(Instance.CoFetch());
        }

        public static void Hide()
        {
            if (Instance == null) return;
            Instance._show = false;
        }

        private void OnGUI()
        {
            if (!SessionGate.Ready) return;
            if (!_show) return;

            _rect = GUILayout.Window(
                GetInstanceID(),
                _rect,
                DrawWindow,
                $"Chat — {SaveId}",
                HighLogic.Skin.window);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // Header
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload", GUILayout.Width(90))) StartCoroutine(CoFetch());
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, HighLogic.Skin.label);
            GUILayout.EndHorizontal();

            HLine(0.22f);
            GUILayout.Space(4f);

            // History
            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _msgs.Count; i++)
            {
                var m = _msgs[i];
                var local = m.Utc.ToLocalTime();
                string stamp = local.ToString("HH:mm", CultureInfo.InvariantCulture);
                GUILayout.Label($"[{stamp}] {m.User}: {m.Text}", _msgStyle, GUILayout.ExpandWidth(true));
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            HLine(0.22f);
            GUILayout.Space(4f);

            // Input row
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("chatInput");
            _input = GUILayout.TextField(_input ?? "", HighLogic.Skin.textField, GUILayout.ExpandWidth(true));
            GUI.enabled = !_busyPost && !string.IsNullOrWhiteSpace(_input);
            if (GUILayout.Button("Send", GUILayout.Width(80)))
                StartCoroutine(CoSend());
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Enter to send
            var ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown &&
                (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
            {
                if (GUI.GetNameOfFocusedControl() == "chatInput" && !_busyPost && !string.IsNullOrWhiteSpace(_input))
                {
                    ev.Use();
                    StartCoroutine(CoSend());
                }
            }

            GUILayout.Space(4f);
            if (GUILayout.Button("Close", GUILayout.Height(24))) _show = false;

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private IEnumerator CoFetch()
        {
            string url = GetUrl();
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();

#pragma warning disable 0618
                if (req.isNetworkError || req.isHttpError)
#pragma warning restore 0618
                {
                    _status = $"Fetch failed: {req.responseCode}";
                    yield break;
                }

                var body = req.downloadHandler.text ?? "";
                if (body == _lastRaw) yield break;

                _lastRaw = body;
                ParseLines(body);
                _status = $"Messages: {_msgs.Count}";
                _scroll.y = float.MaxValue;
            }
        }

        private void ParseLines(string body)
        {
            _msgs.Clear();
            if (string.IsNullOrEmpty(body)) return;

            var lines = body.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                int idx1 = line.IndexOf('|');
                if (idx1 <= 0) continue;
                int idx2 = line.IndexOf('|', idx1 + 1);
                if (idx2 <= 0) continue;

                string tsStr = line.Substring(0, idx1).Trim();
                string userB64 = line.Substring(idx1 + 1, idx2 - idx1 - 1);
                string msgB64 = line.Substring(idx2 + 1);

                DateTime utc;
                if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                    continue;

                string user, msg;
                try
                {
                    user = Encoding.UTF8.GetString(Convert.FromBase64String(userB64));
                    msg = Encoding.UTF8.GetString(Convert.FromBase64String(msgB64));
                }
                catch { continue; }

                msg = msg.Replace("\r\n", " ").Replace('\n', ' ').Trim();

                _msgs.Add(new ChatMsg { Utc = utc, User = user, Text = msg });
            }

            if (_msgs.Count > 500) _msgs.RemoveRange(0, _msgs.Count - 500);
        }

        private IEnumerator CoSend()
        {
            string text = (_input ?? "").Trim();
            if (text.Length == 0) yield break;
            if (text.Length > 300) text = text.Substring(0, 300);

            _busyPost = true;
            _status = "Sending…";

            string url = GetUrl() + "?u=" + Escape(User);
            var bytes = Encoding.UTF8.GetBytes(text);
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");

            yield return req.SendWebRequest();

#pragma warning disable 0618
            if (req.isNetworkError || req.isHttpError)
#pragma warning restore 0618
            {
                _status = $"Send failed: {req.responseCode}";
            }
            else
            {
                _input = "";
                _status = "Sent";
                StartCoroutine(CoFetch());
            }

            _busyPost = false;
            req.Dispose();
        }
    }
}
