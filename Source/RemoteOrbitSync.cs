
// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using KSP.UI.Screens;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public sealed class RemoteOrbitSync : MonoBehaviour
    {
        private const float PollSeconds = 0.5f;

        private readonly Dictionary<string, RemoteMarker> _markers = new Dictionary<string, RemoteMarker>(64);
        private string _url;
        private bool _running;
        private Transform _parent;
        private Material _lineMat;
        private int _scaledLayer;
        private GUIStyle _nameStyle;


        // ---- Visuals (same semantics as your working build) ----
        private static class Visual
        {
            // --- Defaults (inside Visual) ---
            public static float OrbitPx = 2.0f;
            public static float DotPx = 10.0f;

            // World-unit default clamps
            public static float OrbitPxMin = 0.1f;
            public static float OrbitPxMax = 100f;
            public static float DotPxMin = 0.1f;
            public static float DotPxMax = 100f;


            public static int Segments = 180;
        }

        // -------- Menu (same style as Main.cs) --------
        private ApplicationLauncherButton _toolbarButton;
        private Texture2D _fallbackIcon;
        private bool _showMenu = false;
        private Rect _windowRect = new Rect(100, 100, 460, 380);

        // UI mirrors (editable)
        private float _uiOrbitPx, _uiDotPx;
        private int _uiSegments;

        // string buffers so decimals can be typed freely (0.001 etc.)
        private string _uiOrbitMinWorldStr, _uiOrbitMaxWorldStr;
        private string _uiDotMinWorldStr, _uiDotMaxWorldStr;

        private static class Keys
        {
            public const string OrbitPx = "RemoteOrbit_OrbitPx";
            public const string DotPx = "RemoteOrbit_DotPx";
            public const string Segments = "RemoteOrbit_Segments";
            public const string OrbitWorldMin = "RemoteOrbit_OrbitWorldMin";
            public const string OrbitWorldMax = "RemoteOrbit_OrbitWorldMax";
            public const string DotWorldMin = "RemoteOrbit_DotWorldMin";
            public const string DotWorldMax = "RemoteOrbit_DotWorldMax";
        }
        // ---------------------------------------------------------

        private void Start()
        {
            _scaledLayer = LayerMask.NameToLayer("ScaledSpace");
            if (_scaledLayer < 0) _scaledLayer = 10;

            var root = new GameObject("[RemoteOrbitSync]");
            _parent = root.transform;
            if (ScaledSpace.Instance != null) _parent.SetParent(ScaledSpace.Instance.transform, false);
            root.layer = _scaledLayer;

            _lineMat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3000 };
            _url = $"{GlobalConfig.ServerUrl}/orbits/{Uri.EscapeDataString(GlobalConfig.sharedSaveId)}.txt";

            // Load saved settings → mirror to UI
            LoadVisualFromGlobalConfig();

            _uiOrbitPx = Visual.OrbitPx;
            _uiDotPx = Visual.DotPx;
            _uiSegments = Visual.Segments;

            // init decimal-safe text buffers from Visual clamps
            _uiOrbitMinWorldStr = Visual.OrbitPxMin.ToString(CultureInfo.InvariantCulture);
            _uiOrbitMaxWorldStr = Visual.OrbitPxMax.ToString(CultureInfo.InvariantCulture);
            _uiDotMinWorldStr = Visual.DotPxMin.ToString(CultureInfo.InvariantCulture);
            _uiDotMaxWorldStr = Visual.DotPxMax.ToString(CultureInfo.InvariantCulture);

            AddToolbarButton();

            _running = true;
            StartCoroutine(PollLoop());
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        private void OnEnable() { Camera.onPreCull += OnCamPreCull; }
        private void OnDisable() { Camera.onPreCull -= OnCamPreCull; }

        private void OnDestroy()
        {
            _running = false;
            Camera.onPreCull -= OnCamPreCull;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);

            foreach (var kv in _markers) kv.Value.Destroy();
            _markers.Clear();

            if (_parent != null) Destroy(_parent.gameObject);
            RemoveToolbarButton();
        }

        private void OnSceneChange(GameScenes _) => _running = false;

        // -------- Toolbar (same pattern as Main.cs) --------
        private void AddToolbarButton()
        {
            if (ApplicationLauncher.Instance != null && _toolbarButton == null)
            {
                Texture icon = GameDatabase.Instance != null
                    ? GameDatabase.Instance.GetTexture("SimpleMultiplayer/Textures/icon", false)
                    : null;

                if (icon == null)
                {
                    // fallback: small orange square
                    _fallbackIcon = MakeSolid(38, 38, new Color(1f, 0.55f, 0.0f, 1f));
                    icon = _fallbackIcon;
                }

                _toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    () => _showMenu = !_showMenu,
                    () => _showMenu = !_showMenu,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.TRACKSTATION | ApplicationLauncher.AppScenes.MAPVIEW,
                    (Texture)icon
                );
            }
        }

        private void RemoveToolbarButton()
        {
            if (_toolbarButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(_toolbarButton);
                _toolbarButton = null;
            }
            if (_fallbackIcon != null) { Destroy(_fallbackIcon); _fallbackIcon = null; }
        }

        private static Texture2D MakeSolid(int w, int h, Color c)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var arr = new Color[w * h];
            for (int i = 0; i < arr.Length; i++) arr[i] = c;
            t.SetPixels(arr); t.Apply();
            return t;
        }

        // -------- Camera-synced update (no warp ghosting) --------
        private void OnCamPreCull(Camera cam)
        {
            if (!_running) return;
            var mapCam = PlanetariumCamera.Camera;
            if (cam != mapCam || mapCam == null) return;

            foreach (var kv in _markers) kv.Value.Tick(mapCam);
        }

        // -------- Server polling --------
        private IEnumerator PollLoop()
        {
            var wait = new WaitForSecondsRealtime(PollSeconds);
            while (_running)
            {
                yield return DownloadOnce();
                yield return wait;
            }
        }

        private IEnumerator DownloadOnce()
        {
            var www = UnityWebRequest.Get(_url);
            yield return www.SendWebRequest();

            // Only poll when visible contexts are active
            if (!(HighLogic.LoadedScene == GameScenes.TRACKSTATION
                  || (HighLogic.LoadedScene == GameScenes.FLIGHT && MapView.MapIsEnabled)))
                yield break;


            if (!www.isNetworkError && !www.isHttpError)
                ApplySnapshot(www.downloadHandler.text);
        }

        private void ApplySnapshot(string text)
        {
            var seen = new HashSet<string>();

            using (var sr = new StringReader(text ?? ""))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;

                    OrbitRecord rec;
                    if (!OrbitRecord.TryParse(line, out rec)) continue;

                    if (rec.User == GlobalConfig.userName) continue;

                    var body = FlightGlobals.Bodies.FirstOrDefault(b =>
                        string.Equals(b.bodyName, rec.Body, StringComparison.OrdinalIgnoreCase));
                    if (body == null) continue;
                    if (rec.Ecc >= 1.0) continue;

                    // AFTER
                    var ecc = rec.Ecc;
                    if (ecc < 0) ecc = 0;
                    if (ecc >= 1.0) ecc = 0.999999999; // keep elliptical
                    var orbit = new Orbit(
                        rec.IncDeg,
                        (float)ecc,
                        rec.SMA,
                        rec.LanDeg,
                        rec.ArgpDeg,
                        rec.MnaRad,                    // ✅ radians
                        rec.EpochUT,
                        body);

                    var key = rec.User; // P0: one per user
                    seen.Add(key);

                    RemoteMarker mk;
                    if (!_markers.TryGetValue(key, out mk))
                    {
                        mk = new RemoteMarker(_parent, _lineMat, _scaledLayer, rec.User, rec.Vessel, rec.Color, Visual.Segments); // <- +rec.Vessel
                        _markers[key] = mk;
                    }
                    mk.SetOrbit(orbit, rec.EpochUT);
                    mk.SetVessel(rec.Vessel); // <- keep label fresh if vessel name changes

                }
            }

            var toRemove = _markers.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var k in toRemove)
            {
                _markers[k].Destroy();
                _markers.Remove(k);
            }
        }

        // -------- IMGUI window (like Main.cs) --------
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
                _showMenu = !_showMenu;
        }

        private void OnGUI()
        {
            if (!(HighLogic.LoadedScene == GameScenes.TRACKSTATION || (HighLogic.LoadedScene == GameScenes.FLIGHT && MapView.MapIsEnabled)))
                return;

            if (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint)
                GUI.skin = HighLogic.Skin;

            // 1) Always draw name labels near markers
            DrawNameLabels();

            // 2) Settings window (only when toggled)
            if (_showMenu)
                _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawSettingsMenu, "Remote Orbit – Settings");
        }


        private void DrawSettingsMenu(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Line thickness (pixels)");
            _uiOrbitPx = GUILayout.HorizontalSlider(_uiOrbitPx, 0.1f, 12f);
            GUILayout.Label($"OrbitPx: {_uiOrbitPx:F2}");

            GUILayout.Space(6);
            GUILayout.Label("Marker size (pixels)");
            _uiDotPx = GUILayout.HorizontalSlider(_uiDotPx, 0.1f, 24f);
            GUILayout.Label($"DotPx: {_uiDotPx:F2}");

            GUILayout.Space(6);
            GUILayout.Label("Orbit smoothness (segments)");
            int seg = Mathf.RoundToInt(GUILayout.HorizontalSlider(_uiSegments, 64f, 360f));
            seg = Mathf.Clamp(seg, 64, 360);
            if (seg != _uiSegments) _uiSegments = seg;
            GUILayout.Label($"Segments: {_uiSegments}");

            GUILayout.Space(8);
            GUILayout.Label("<b>World clamps (units)</b>");
            GUILayout.Label("Allowed range: 0.001 – 100.0");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Line min", GUILayout.Width(80));
            _uiOrbitMinWorldStr = GUILayout.TextField(_uiOrbitMinWorldStr, GUILayout.Width(100));
            GUILayout.Label("max", GUILayout.Width(30));
            _uiOrbitMaxWorldStr = GUILayout.TextField(_uiOrbitMaxWorldStr, GUILayout.Width(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Marker min", GUILayout.Width(80));
            _uiDotMinWorldStr = GUILayout.TextField(_uiDotMinWorldStr, GUILayout.Width(100));
            GUILayout.Label("max", GUILayout.Width(30));
            _uiDotMaxWorldStr = GUILayout.TextField(_uiDotMaxWorldStr, GUILayout.Width(100));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply")) { ApplyUi(); }
            if (GUILayout.Button("Save")) { ApplyUi(); SaveVisualToGlobalConfig(); }
            if (GUILayout.Button("Reset"))
            {
                // defaults you prefer here
                _uiOrbitPx = 2.0f; _uiDotPx = 10.0f; _uiSegments = 360;

                // reset strings from current Visual clamps (not hard-coded)
                _uiOrbitMinWorldStr = Visual.OrbitPxMin.ToString(CultureInfo.InvariantCulture);
                _uiOrbitMaxWorldStr = Visual.OrbitPxMax.ToString(CultureInfo.InvariantCulture);
                _uiDotMinWorldStr = Visual.DotPxMin.ToString(CultureInfo.InvariantCulture);
                _uiDotMaxWorldStr = Visual.DotPxMax.ToString(CultureInfo.InvariantCulture);

                ApplyUi();
            }
            if (GUILayout.Button("Close")) _showMenu = false;
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawNameLabels()
        {
            var cam = PlanetariumCamera.Camera;
            if (cam == null) return;

            if (_nameStyle == null)
            {
                // Use KSP skin label, keep defaults to avoid TextRenderingModule types
                _nameStyle = new GUIStyle(HighLogic.Skin.label)
                {
                    fontSize = 12,           // safe: int
                    richText = false,        // safe: bool
                    clipping = TextClipping.Clip // in UnityEngine (no extra ref)
                };
                // (No alignment/fontStyle here → avoids TextRenderingModule)
            }

            foreach (var kv in _markers)
            {
                var mk = kv.Value;
                if (!mk.HasScreenPoint) continue;

                // Place a little to the right of the dot
                Vector2 p = mk.ScreenLabelPos + new Vector2(10f, -4f);
                var rect = new Rect(p.x, p.y, 260f, 18f);

                // Shadow for readability
                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), mk.UserName, _nameStyle);

                // Colored label (player color)
                GUI.color = mk.UserColor;
                GUI.Label(rect, mk.LabelText, _nameStyle);
                GUI.color = prev;
            }
        }



        private static float ParseUiFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().Replace(',', '.'); // allow DE input
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private void ApplyUi()
        {
            bool segChanged = (_uiSegments != Visual.Segments);

            // pixel targets
            Visual.OrbitPx = _uiOrbitPx;
            Visual.DotPx = _uiDotPx;

            // parse text fields to floats (use current Visual as fallbacks)
            float newOrbitMin = ParseUiFloat(_uiOrbitMinWorldStr, Visual.OrbitPxMin);
            float newOrbitMax = ParseUiFloat(_uiOrbitMaxWorldStr, Visual.OrbitPxMax);
            float newDotMin = ParseUiFloat(_uiDotMinWorldStr, Visual.DotPxMin);
            float newDotMax = ParseUiFloat(_uiDotMaxWorldStr, Visual.DotPxMax);

            // enforce allowed range 0.01–100
            newOrbitMin = Mathf.Clamp(newOrbitMin, 0.01f, 100f);
            newOrbitMax = Mathf.Clamp(newOrbitMax, 0.01f, 100f);
            newDotMin = Mathf.Clamp(newDotMin, 0.01f, 100f);
            newDotMax = Mathf.Clamp(newDotMax, 0.01f, 100f);

            // enforce ordering
            if (newOrbitMax < newOrbitMin) newOrbitMax = newOrbitMin;
            if (newDotMax < newDotMin) newDotMax = newDotMin;

            // commit to Visual + normalize strings
            Visual.OrbitPxMin = newOrbitMin; _uiOrbitMinWorldStr = newOrbitMin.ToString(CultureInfo.InvariantCulture);
            Visual.OrbitPxMax = newOrbitMax; _uiOrbitMaxWorldStr = newOrbitMax.ToString(CultureInfo.InvariantCulture);
            Visual.DotPxMin = newDotMin; _uiDotMinWorldStr = newDotMin.ToString(CultureInfo.InvariantCulture);
            Visual.DotPxMax = newDotMax; _uiDotMaxWorldStr = newDotMax.ToString(CultureInfo.InvariantCulture);

            // segments
            _uiSegments = Mathf.Clamp(_uiSegments, 64, 360);
            if (segChanged)
            {
                Visual.Segments = _uiSegments;
                foreach (var kv in _markers) kv.Value.SetSegments(Visual.Segments);
            }
        }

        private void LoadVisualFromGlobalConfig()
        {
            try
            {
                var path = Path.Combine(KSPUtil.ApplicationRootPath, GlobalConfig.ConfigFilePath);
                if (!File.Exists(path)) return;

                var cn = ConfigNode.Load(path);
                if (cn == null) return;
                var inv = CultureInfo.InvariantCulture;

                float f; int i;
                if (cn.HasValue(Keys.OrbitPx) && float.TryParse(cn.GetValue(Keys.OrbitPx), NumberStyles.Float, inv, out f)) Visual.OrbitPx = f;
                if (cn.HasValue(Keys.DotPx) && float.TryParse(cn.GetValue(Keys.DotPx), NumberStyles.Float, inv, out f)) Visual.DotPx = f;
                if (cn.HasValue(Keys.Segments) && int.TryParse(cn.GetValue(Keys.Segments), NumberStyles.Integer, inv, out i)) Visual.Segments = Mathf.Clamp(i, 64, 512);
                if (cn.HasValue(Keys.OrbitWorldMin) && float.TryParse(cn.GetValue(Keys.OrbitWorldMin), NumberStyles.Float, inv, out f)) Visual.OrbitPxMin = Mathf.Max(0f, f);
                if (cn.HasValue(Keys.OrbitWorldMax) && float.TryParse(cn.GetValue(Keys.OrbitWorldMax), NumberStyles.Float, inv, out f)) Visual.OrbitPxMax = Mathf.Max(0f, f);
                if (cn.HasValue(Keys.DotWorldMin) && float.TryParse(cn.GetValue(Keys.DotWorldMin), NumberStyles.Float, inv, out f)) Visual.DotPxMin = Mathf.Max(0f, f);
                if (cn.HasValue(Keys.DotWorldMax) && float.TryParse(cn.GetValue(Keys.DotWorldMax), NumberStyles.Float, inv, out f)) Visual.DotPxMax = Mathf.Max(0f, f);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteOrbitSync] LoadVisualFromGlobalConfig failed: {e.Message}");
            }
        }

        private void SaveVisualToGlobalConfig()
        {
            try
            {
                var path = Path.Combine(KSPUtil.ApplicationRootPath, GlobalConfig.ConfigFilePath);
                var cn = File.Exists(path) ? ConfigNode.Load(path) : new ConfigNode();

                SetOrAdd(cn, Keys.OrbitPx, Visual.OrbitPx.ToString(CultureInfo.InvariantCulture));
                SetOrAdd(cn, Keys.DotPx, Visual.DotPx.ToString(CultureInfo.InvariantCulture));
                SetOrAdd(cn, Keys.Segments, Visual.Segments.ToString());
                SetOrAdd(cn, Keys.OrbitWorldMin, Visual.OrbitPxMin.ToString(CultureInfo.InvariantCulture));
                SetOrAdd(cn, Keys.OrbitWorldMax, Visual.OrbitPxMax.ToString(CultureInfo.InvariantCulture));
                SetOrAdd(cn, Keys.DotWorldMin, Visual.DotPxMin.ToString(CultureInfo.InvariantCulture));
                SetOrAdd(cn, Keys.DotWorldMax, Visual.DotPxMax.ToString(CultureInfo.InvariantCulture));

                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                cn.Save(path);
                ScreenMessages.PostScreenMessage("<b><color=#88FF88>Saved Remote Orbit settings</color></b>", 2f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteOrbitSync] SaveVisualToGlobalConfig failed: {e.Message}");
            }
        }

        private static void SetOrAdd(ConfigNode cn, string key, string value)
        {
            if (cn.HasValue(key)) cn.SetValue(key, value); else cn.AddValue(key, value);
        }

        // ---------- Data ----------
        private struct OrbitRecord
        {
            public string User, Vessel, Body, ColorHex;
            public double EpochUT, SMA, Ecc, IncDeg, LanDeg, ArgpDeg, MnaRad, UpdatedUT;

            public Color Color
            {
                get
                {
                    Color c;
                    // 1) Prefer sender-provided hex (supports #RRGGBB and #RRGGBBAA)
                    if (!string.IsNullOrEmpty(ColorHex) && ColorUtility.TryParseHtmlString(ColorHex, out c)) return c;

                    // 2) Optional local fallback (older uploaders may not send a color)
                    var local = (GlobalConfig.userColorHex ?? "").Trim();
                    if (!string.IsNullOrEmpty(local))
                    {
                        if (local[0] != '#') local = "#" + local;
                        if (ColorUtility.TryParseHtmlString(local, out c)) return c;
                    }

                    // 3) Stable per-user hash (last resort)
                    return HashColor(User);
                }
            }


            public static bool TryParse(string line, out OrbitRecord rec)
            {
                rec = default(OrbitRecord);
                var p = SplitCsv(line);
                if (p.Count < 12) return false;
                try
                {
                    var inv = CultureInfo.InvariantCulture;
                    rec.User = p[0].Trim();
                    rec.Vessel = p[1].Trim();
                    rec.Body = p[2].Trim();
                    rec.EpochUT = double.Parse(p[3], inv);
                    rec.SMA = double.Parse(p[4], inv);
                    rec.Ecc = double.Parse(p[5], inv);
                    rec.IncDeg = double.Parse(p[6], inv);
                    rec.LanDeg = double.Parse(p[7], inv);
                    rec.ArgpDeg = double.Parse(p[8], inv);
                    rec.MnaRad = double.Parse(p[9], inv);
                    rec.ColorHex = p[10].Trim();
                    rec.UpdatedUT = double.Parse(p[11], inv);
                    return true;
                }
                catch { return false; }
            }

            private static List<string> SplitCsv(string s)
            {
                var list = new List<string>(16);
                int start = 0;
                for (int i = 0; i < s.Length; i++)
                    if (s[i] == ',') { list.Add(s.Substring(start, i - start)); start = i + 1; }
                list.Add(s.Substring(start));
                return list;
            }
        }

        // ---------- Rendering ----------
        private sealed class RemoteMarker
        {
            private readonly GameObject _root;
            private readonly LineRenderer _lr;
            private readonly GameObject _dot;
            private int _segments;

            private Orbit _orbit;
            private Gradient _grad;

            private readonly string _user;
            private string _vessel;
            private readonly Color _baseColor;
            private Vector3 _lastScreen; // from cam.WorldToScreenPoint(...)
            private double _snapshotUT;
            public RemoteMarker(Transform parent, Material lineMat, int scaledLayer, string user, string vessel, Color color, int segments)
            {
                _segments = segments;

                _root = new GameObject($"[RemoteMarker]{user}");
                _root.transform.SetParent(parent, false);
                _root.layer = scaledLayer;

                _lr = _root.AddComponent<LineRenderer>();
                _lr.material = lineMat;
                _lr.loop = true;
                _lr.positionCount = 0;
                _lr.useWorldSpace = true; // ScaledSpace WORLD

                // Directional gradient: head bright -> tail fades
                BuildGradient(color);
                _lr.colorGradient = _grad;
                // User Names
                _user = user;
                _vessel = vessel ?? "";
                _baseColor = color;

                _dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _dot.name = "marker";
                _dot.transform.SetParent(_root.transform, false);
                _dot.layer = scaledLayer;
                var mr = _dot.GetComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Unlit/Color")); mat.color = color;
                mr.sharedMaterial = mat;
            }
            public void SetVessel(string vessel) { _vessel = vessel ?? ""; }
            public void SetOrbit(Orbit orbit, double snapshotUT)
            {
                _orbit = orbit;
                _snapshotUT = snapshotUT;
                RebuildWorldLine(); // keep your existing path builder
            }


            public void SetSegments(int segments)
            {
                segments = Mathf.Clamp(segments, 64, 512);
                if (segments == _segments) return;
                _segments = segments;
                RebuildWorldLine(); // parameterless overload computes head
            }

            public void Tick(Camera cam)
            {
                if (_orbit == null || cam == null) return;

                double ut = _snapshotUT;
                Vector3d relNow = _orbit.getRelativePositionAtUT(ut);
                Vector3d worldNow = _orbit.referenceBody.position + new Vector3d(relNow.x, relNow.z, relNow.y); // Y/Z swap
                Vector3 scaledNow = ScaledSpace.LocalToScaledSpace(worldNow);
                _dot.transform.position = scaledNow;

                _lastScreen = cam.WorldToScreenPoint(scaledNow);

                float w = PixelsToWorldAt(cam, scaledNow, Visual.OrbitPx);
                float d = PixelsToWorldAt(cam, scaledNow, Visual.DotPx);
                _lr.widthMultiplier = Mathf.Clamp(w, Visual.OrbitPxMin, Visual.OrbitPxMax);
                _dot.transform.localScale = Vector3.one * Mathf.Clamp(d, Visual.DotPxMin, Visual.DotPxMax);

                RebuildWorldLine(scaledNow);
            }


            // ---- Overload #1: compute head (used by SetOrbit/SetSegments) ----
            private void RebuildWorldLine()
            {
                if (_orbit == null || !(_orbit.eccentricity >= 0 && _orbit.eccentricity < 1.0))
                {
                    _lr.positionCount = 0;
                    return;
                }

                double headUT = _snapshotUT; // was: Planetarium.GetUniversalTime()
                Vector3d relNow = _orbit.getRelativePositionAtUT(headUT);
                Vector3d worldNow = _orbit.referenceBody.position + new Vector3d(relNow.x, relNow.z, relNow.y);
                Vector3 scaledNow = ScaledSpace.LocalToScaledSpace(worldNow);

                RebuildWorldLine(scaledNow);
            }


            // ---- Overload #2: take head explicitly (used each frame in Tick) ----
            private void RebuildWorldLine(Vector3 scaledHead)
            {
                if (_orbit == null || !(_orbit.eccentricity >= 0 && _orbit.eccentricity < 1.0))
                { _lr.positionCount = 0; return; }

                var body = _orbit.referenceBody;
                if (_lr.positionCount != _segments) _lr.positionCount = _segments;

                // 1) Sample all points
                Vector3[] pts = new Vector3[_segments];
                int headIdx = 0;
                float bestSqr = float.PositiveInfinity;

                for (int i = 0; i < _segments; i++)
                {
                    double ta = (i / (double)_segments) * Math.PI * 2.0; // true anomaly
                    Vector3d rel = _orbit.getRelativePositionFromTrueAnomaly(ta);
                    Vector3d world = body.position + new Vector3d(rel.x, rel.z, rel.y); // keep Y/Z swap
                    Vector3 scaled = ScaledSpace.LocalToScaledSpace(world);
                    pts[i] = scaled;

                    // find closest vertex to current head
                    float sq = (scaled - scaledHead).sqrMagnitude;
                    if (sq < bestSqr) { bestSqr = sq; headIdx = i; }
                }

                // 2) Rotate so index 0 is the "head"
                int outIdx = 0;
                for (int i = headIdx; i < _segments; i++) _lr.SetPosition(outIdx++, pts[i]);
                for (int i = 0; i < headIdx; i++) _lr.SetPosition(outIdx++, pts[i]);
            }

            private static float PixelsToWorldAt(Camera cam, Vector3 scaledPos, float pixels)
            {
                float dist = Vector3.Distance(cam.transform.position, scaledPos);
                float height = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                return pixels * (height / Mathf.Max(1, Screen.height));
            }

            private void BuildGradient(Color baseColor)
            {
                // Head bright, tail dimmer
                var head = baseColor; head.a = 1.00f;
                var mid = Color.Lerp(baseColor, Color.black, 0.25f); mid.a = 0.70f;
                var tail = Color.Lerp(baseColor, Color.black, 0.45f); tail.a = 0.30f;

                _grad = new Gradient();
                _grad.SetKeys(
                    new[]
                    {
                new GradientColorKey(tail, 0.00f),
                new GradientColorKey(mid,  0.25f),
                new GradientColorKey(head, 1.00f),
                    },
                    new[]
                    {
                new GradientAlphaKey(tail.a, 0.00f),
                new GradientAlphaKey(mid.a,  0.25f),
                new GradientAlphaKey(head.a, 1.00f),
                    }
                );
            }

            public void Destroy()
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }

            public bool HasScreenPoint => _lastScreen.z > 0f;
            public Vector2 ScreenLabelPos => new Vector2(_lastScreen.x, Screen.height - _lastScreen.y); // OnGUI coords
            public string UserName => _user;
            public string LabelText => string.IsNullOrEmpty(_vessel) ? _user : (_user + " — " + _vessel);
            public Color UserColor => _baseColor;

        }


        private static Color HashColor(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 16777619;
                float hue = (h % 360u) / 360f;
                return Color.HSVToRGB(hue, 0.85f, 1f);
            }
        }
    }
}
