// Copyright (c) 2025 Julius Brockelmann
// SPDX-License-Identifier: MIT
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

using UnityEngine;
using KSP.UI.Screens;
using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using System.IO;
using System.Text;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class Main : MonoBehaviour
    {
        public static Main Instance { get; private set; }
        public string[] vesselFiles = new string[0];

        private ApplicationLauncherButton toolbarButton;
        private bool showMenu = false;
        private bool showSettings = false;
        private Rect windowRect = new Rect(100, 100, 400, 300);
        private VesselExporter vesselExporter;
        private Vector2 scrollPosition = Vector2.zero;
        private FlagSync flagSync;
        // --- Color menu state ---
        private string _colorHexInput;    // text field backing store
        private bool _colorValid = true;  // validation flag
        private Color _parsedColor = Color.white;
        private static ApplicationLauncherButton s_Button;
        private static GUIStyle _richLabel;

        // Presence viewer
        private bool showPresence = false;
        private Rect presenceRect = new Rect(520, 100, 250, 270);
        private Vector2 presenceScroll = Vector2.zero;

        // scansat viewer
        private bool showScanSat = false;
        private Rect scansatRect = new Rect(540, 100, 480, 360);
        private Vector2 scansatScroll = Vector2.zero;
        private List<string> scansatLines = new List<string>();

        // model for listing + inject scansat datza
        private struct ScanEntry { public string user, body, types; }
        private List<ScanEntry> scansatEntries = new List<ScanEntry>();
        private bool injectingScan = false; // simple reentrancy guard

        private static string AgeYMD(double epochSec)
        {
            if (epochSec <= 0) return "0y 0m 0d";
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var dt = epoch.AddSeconds(epochSec);
            var now = DateTime.UtcNow;
            if (dt > now) return "0y 0m 0d";
            var days = (int)(now - dt).TotalDays;
            int y = days / 365; days %= 365;
            int m = days / 30; int d = days % 30;
            return $"{y}y {m}m {d}d";
        }
        private static string UtYMD(double ut)
        {
            if (ut <= 0) return "Y0 M0 D0";

            if (GameSettings.KERBIN_TIME) // Kerbin: 6h day, 426-day year
            {
                const int DaySec = 6 * 60 * 60;           // 21,600
                const int YearDays = 426;
                const int YearSec = DaySec * YearDays;

                long s = (long)ut;
                int year = (int)(s / YearSec);
                int dayOfYear = (int)((s % YearSec) / DaySec);

                // 12 “months”: 11×35 + 41 days
                int[] lens = { 35, 35, 35, 35, 35, 35, 35, 35, 35, 35, 35, 41 };
                int month = 1;
                int d = dayOfYear;
                for (int i = 0; i < lens.Length; i++)
                {
                    if (d >= lens[i]) { d -= lens[i]; month++; }
                    else break;
                }
                int day = d + 1;
                return $"Y{year} M{month} D{day}";
            }
            else // Earth-style: 24h day, 365-day year (no leap)
            {
                const int DaySec = 24 * 60 * 60;          // 86,400
                const int YearDays = 365;
                const int YearSec = DaySec * YearDays;

                long s = (long)ut;
                int year = (int)(s / YearSec);
                int dayOfYear = (int)((s % YearSec) / DaySec);

                int[] lens = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
                int month = 1;
                int d = dayOfYear;
                for (int i = 0; i < lens.Length; i++)
                {
                    if (d >= lens[i]) { d -= lens[i]; month++; }
                    else break;
                }
                int day = d + 1;
                return $"Y{year} M{month} D{day}";
            }
        }
        private static string KerbinYMD(double ut)
        {
            const int SecondsPerDay = 6 * 60 * 60; // 21,600
            const int DaysPerYear = 426;

            if (ut <= 0) return "Y1 D1"; // match stock KSP UI

            long s = (long)Math.Floor(ut);
            long secondsPerYear = SecondsPerDay * (long)DaysPerYear;

            int year = (int)(s / secondsPerYear) + 1;                 // 1-based year
            int day = (int)((s / SecondsPerDay) % DaysPerYear) + 1;  // 1..426
            return $"Y{year} D{day}";
        }
        private void Awake()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnAppLauncherDestroyed);
        }
        private static string NormalizeHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s[0] == '#' ? s.ToUpperInvariant() : ("#" + s.ToUpperInvariant());
        }

        // one-pixel swatch texture for color preview
        private static Texture2D _swatchTex;
        private static void EnsureSwatchTex()
        {
            if (_swatchTex != null) return;
            _swatchTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _swatchTex.SetPixel(0, 0, Color.white);
            _swatchTex.filterMode = FilterMode.Point;
            _swatchTex.wrapMode = TextureWrapMode.Clamp;
            _swatchTex.Apply();
        }

        private void Start()
        {
            Instance = this;
            GlobalConfig.LoadConfig();
            vesselExporter = new VesselExporter(this);
            InvokeRepeating(nameof(RefreshVesselList), 0f, 5f); // Call every 5 seconds
            Debug.Log("Simple Vessel Export/Import Mod Loaded.");

            flagSync = new FlagSync(this); // Initialize FlagSync
            InvokeRepeating(nameof(SynchronizeFlags), 0f, 5f); // Synchronize flags every 5 seconds
            InvokeRepeating(nameof(RefreshScanSatList), 0f, 10f);
        }
        private void RefreshVesselList() { if (!SessionGate.Ready) return; StartCoroutine(vesselExporter.LoadVesselFilesFromServer()); }
        private void SynchronizeFlags() { if (!SessionGate.Ready) return; StartCoroutine(flagSync.SynchronizeFlags()); }
        private void RefreshScanSatList() { if (!SessionGate.Ready) return; StartCoroutine(FetchScanSatList()); }
        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnAppLauncherDestroyed);

            if (s_Button != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(s_Button);
                s_Button = null;
            }

            CancelInvoke(nameof(RefreshVesselList));
            CancelInvoke(nameof(SynchronizeFlags));
            CancelInvoke(nameof(RefreshScanSatList));
        }

        private void RemoveToolbarButton()
        {
            if (toolbarButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                toolbarButton = null;
            }
        }

        private void OnGUI()
        {
            if (!SessionGate.Ready) return;

            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.EDITOR) return;

            if (showMenu)
            {
                windowRect = GUILayout.Window(0, windowRect, DrawMenu, "Vessel Export/Import");
            }

            if (showSettings)
            {
                windowRect = GUILayout.Window(1, windowRect, DrawSettingsMenu, "Settings");
            }
            if (showPresence)
            {
                presenceRect = GUILayout.Window(2, presenceRect, DrawPresenceWindow, "Players Online");
            }
            if (showScanSat)
                scansatRect = GUILayout.Window(3, scansatRect, DrawScanSatWindow, "ScanSat");

        }

        private void OnAppLauncherReady()
        {
            if (ApplicationLauncher.Instance == null) return;
            if (s_Button != null) return;

            s_Button = ApplicationLauncher.Instance.AddModApplication(
                onTrue: () => { showMenu = true; showSettings = false; },
                onFalse: () => { showMenu = false; showSettings = false; },
                null, null, null, null,
                ApplicationLauncher.AppScenes.SPACECENTER
              | ApplicationLauncher.AppScenes.FLIGHT
              | ApplicationLauncher.AppScenes.VAB
              | ApplicationLauncher.AppScenes.SPH,
                GameDatabase.Instance.GetTexture("SimpleMultiplayer/Textures/icon", false)
            );
        }

        private void OnAppLauncherDestroyed()
        {
            s_Button = null; // launcher rebuilt; allow recreate
        }

        private void DrawMenu(int windowID)
        {
            GUILayout.BeginVertical();
            if (GUILayout.Button("Open Player Viewer")) showPresence = true;
            if (GUILayout.Button("Open Chat")) SimpleMultiplayer.Chat.Show();
            if (GUILayout.Button("ScanSat")) showScanSat = true;
            GUILayout.Space(6);

            bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
            GUI.enabled = inFlight;
            if (GUILayout.Button("Export Vessel"))
            {
                StartCoroutine(vesselExporter.ExportVesselToServer());
            }
            GUI.enabled = true;

            GUILayout.Space(6);

            GUILayout.Label("Available Vessels on Server:");

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(150));
            if (vesselFiles.Length > 0)
            {
                foreach (var fileWithUser in vesselFiles)
                {
                    if (string.IsNullOrWhiteSpace(fileWithUser)) continue;

                    string[] parts = fileWithUser.Split(':');
                    if (parts.Length == 2)
                    {
                        string user = parts[0].Trim();
                        string vessel = parts[1].Trim();

                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button($"{user}: {vessel}"))
                        {
                            StartCoroutine(vesselExporter.ImportVesselFromServer(user, vessel));
                        }

                        if (GUILayout.Button("Delete", GUILayout.Width(80)))
                        {
                            StartCoroutine(vesselExporter.DeleteVesselFromServer(user, vessel));
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                GUILayout.Label("No vessels found on the server.");
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("Settings"))
            {
                showSettings = true;
                showMenu = false;
            }
            if (GUILayout.Button("Close"))
            {
                showMenu = false;
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawScanSatWindow(int id)
        {
            GUILayout.BeginVertical();

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(100))) RefreshScanSatList();
                GUILayout.FlexibleSpace();
                GUI.enabled = !injectingScan;
                GUILayout.Label(injectingScan ? "Injecting..." : "");
                GUI.enabled = true;
            }
            GUILayout.Space(4);

            scansatScroll = GUILayout.BeginScrollView(scansatScroll, GUILayout.Height(280));
            if (scansatEntries == null || scansatEntries.Count == 0)
            {
                GUILayout.Label("No SCAN data found.");
            }
            else
            {
                for (int i = 0; i < scansatEntries.Count; i++)
                {
                    var e = scansatEntries[i];
                    GUI.enabled = !injectingScan;
                    if (GUILayout.Button($"{e.user} - {e.body} - {e.types}"))
                        StartCoroutine(InjectScanFromUser(e.user));
                    GUI.enabled = true;
                }
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("Close")) showScanSat = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }



        private IEnumerator FetchScanSatList()
        {
            var baseUrl = GlobalConfig.ServerUrl;
            var saveId = GlobalConfig.sharedSaveId;

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(saveId))
                yield break;

            var users = new List<string>();
            // presence JSON
            using (var req = UnityWebRequest.Get(baseUrl.TrimEnd('/') + "/presence?format=json"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 6;
                yield return req.SendWebRequest();
                if (!req.isNetworkError && !req.isHttpError)
                    users = ParseUsersFromPresenceJson(req.downloadHandler.text);
            }

            if (users.Count == 0 && !string.IsNullOrEmpty(GlobalConfig.userName))
                users.Add(GlobalConfig.userName); // fallback

            var entries = new List<ScanEntry>();
            for (int ui = 0; ui < users.Count; ui++)
            {
                string u = users[ui];
                string url = baseUrl.TrimEnd('/') + "/scenarios/" + UnityWebRequest.EscapeURL(saveId)
                           + "/SCANcontroller?user=" + UnityWebRequest.EscapeURL(u);

                using (var get = UnityWebRequest.Get(url))
                {
                    get.downloadHandler = new DownloadHandlerBuffer();
                    get.timeout = 6;
                    yield return get.SendWebRequest();
                    if (get.isNetworkError || get.isHttpError) continue;

                    string text = get.downloadHandler.text ?? "";
                    if (string.IsNullOrEmpty(text)) continue;

                    var node = ParseConfigFromString_Unique(text);
                    if (node == null) continue;

                    // bodies: find any Body{Name=...} anywhere
                    var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in GetNodesDeep(node, "Body"))
                    {
                        string bn = b.GetValue("Name") ?? b.GetValue("name");
                        if (!string.IsNullOrEmpty(bn)) bodies.Add(bn);
                    }
                    if (bodies.Count == 0) bodies.Add("Unknown");

                    // scan types: flatten all Sensor.type bitmasks into a single set
                    var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in GetNodesDeep(node, "Sensor"))
                    {
                        string t = s.GetValue("type") ?? s.GetValue("Type");
                        if (!string.IsNullOrEmpty(t)) AddScanTypesTo(types, t);
                    }
                    var typeList = new List<string>(types);
                    typeList.Sort(StringComparer.OrdinalIgnoreCase);
                    string typeJoined = typeList.Count > 0 ? string.Join(", ", typeList) : "Unknown";

                    foreach (var body in bodies)
                        entries.Add(new ScanEntry { user = u, body = body, types = typeJoined });
                }
            }
            entries.Sort((a, b) =>
            {
                int u = string.Compare(a.user, b.user, StringComparison.OrdinalIgnoreCase);
                if (u != 0) return u;
                int bo = string.Compare(a.body, b.body, StringComparison.OrdinalIgnoreCase);
                if (bo != 0) return bo;
                return string.Compare(a.types, b.types, StringComparison.OrdinalIgnoreCase);
            });
            scansatEntries = entries;

        }

        private IEnumerator InjectScanFromUser(string user)
        {
            if (injectingScan) yield break;
            injectingScan = true;

            // keep it simple: only from Space Center like ScenarioSync
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                ScreenMessages.PostScreenMessage("Open Space Center to import SCAN data.", 3f, ScreenMessageStyle.UPPER_CENTER);
                injectingScan = false; yield break;
            }

            var baseUrl = GlobalConfig.ServerUrl;
            var saveId = GlobalConfig.sharedSaveId;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(saveId) || string.IsNullOrEmpty(user))
            { injectingScan = false; yield break; }

            string url = baseUrl.TrimEnd('/') + "/scenarios/" + UnityWebRequest.EscapeURL(saveId)
                       + "/SCANcontroller?user=" + UnityWebRequest.EscapeURL(user);

            using (var get = UnityWebRequest.Get(url))
            {
                get.downloadHandler = new DownloadHandlerBuffer();
                get.timeout = 8;
                yield return get.SendWebRequest();

#pragma warning disable CS0618
                if (get.isNetworkError || get.isHttpError)
#pragma warning restore CS0618
                {
                    ScreenMessages.PostScreenMessage("SCAN import failed: HTTP error", 3f, ScreenMessageStyle.UPPER_CENTER);
                    injectingScan = false; yield break;
                }

                string text = (get.downloadHandler != null) ? (get.downloadHandler.text ?? "") : "";
                if (string.IsNullOrEmpty(text))
                {
                    ScreenMessages.PostScreenMessage("SCAN import failed: empty file", 3f, ScreenMessageStyle.UPPER_CENTER);
                    injectingScan = false; yield break;
                }

                // parse whatever we got
                var parsed = ParseConfigFromString_Unique(text);
                if (parsed == null)
                {
                    ScreenMessages.PostScreenMessage("SCAN import failed: parse error", 3f, ScreenMessageStyle.UPPER_CENTER);
                    injectingScan = false; yield break;
                }

                // find the SCANcontroller node anywhere
                var scanNode = FindFirstNodeDeep(parsed, "SCANcontroller");
                if (scanNode == null)
                {
                    ScreenMessages.PostScreenMessage("SCAN import failed: SCAN node not found", 3f, ScreenMessageStyle.UPPER_CENTER);
                    injectingScan = false; yield break;
                }

                // build a proper SCENARIO wrapper like ScenarioSync does
                var scen = new ConfigNode("SCENARIO");
                scen.AddValue("name", "SCANcontroller");
                scen.AddValue("scene", "5, 6, 7, 8"); // SpaceCenter, Flight, TrackingStation, Editor
                foreach (ConfigNode.Value v in scanNode.values) scen.AddValue(v.name, v.value);
                foreach (ConfigNode n in scanNode.nodes) scen.AddNode(n.CreateCopy());

                // get or create the ProtoScenarioModule for SCANcontroller
                var proto = FindProtoScenario("SCANcontroller");
                if (proto == null)
                {
                    var t = FindType("SCANsat.SCANcontroller");
                    if (t == null)
                    {
                        ScreenMessages.PostScreenMessage("SCAN import failed: SCANsat not found", 3f, ScreenMessageStyle.UPPER_CENTER);
                        injectingScan = false; yield break;
                    }
                    proto = HighLogic.CurrentGame.AddProtoScenarioModule(
                                t,
                                new[] { GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER, GameScenes.EDITOR });
                    // ensure moduleRef exists
                    proto.Load(ScenarioRunner.Instance);
                }

                // inject into live module and overwrite proto, just like ScenarioSync
                if (proto.moduleRef != null)
                    proto.moduleRef.OnLoad(scen);
                proto.Save(scen.CreateCopy());

                ScreenMessages.PostScreenMessage($"Imported SCAN from {user}", 2.5f, ScreenMessageStyle.UPPER_CENTER);
            }

            injectingScan = false;
        }

        private static ProtoScenarioModule FindProtoScenario(string moduleName)
        {
            var list = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.scenarios : null;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].moduleName, moduleName, StringComparison.Ordinal))
                    return list[i];
            return null;
        }

        private static Type FindType(string fullName)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                var t = asms[i].GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static ConfigNode ParseConfigFromString_Unique(string text)
        {
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SimpleMultiplayer", "PluginData");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "scan_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(file, text ?? "", Encoding.UTF8);
                var node = ConfigNode.Load(file);
                try { File.Delete(file); } catch { }
                return node;
            }
            catch { return null; }
        }

        private static ConfigNode FindFirstNodeDeep(ConfigNode node, string target)
        {
            if (node == null) return null;
            if (string.Equals(node.name, target, StringComparison.OrdinalIgnoreCase)) return node;
            var kids = node.nodes;
            if (kids != null)
                for (int i = 0; i < kids.Count; i++)
                {
                    var f = FindFirstNodeDeep(kids[i], target);
                    if (f != null) return f;
                }
            return null;
        }

        private static ScenarioModule FindScanController()
        {
            var scenarios = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.scenarios : null;
            if (scenarios == null) return null;
            for (int i = 0; i < scenarios.Count; i++)
            {
                var ps = scenarios[i];
                if (ps != null && ps.moduleName == "SCANcontroller" && ps.moduleRef != null)
                    return ps.moduleRef;
            }
            return null;
        }

        private static List<string> ParseUsersFromPresenceJson(string json)
        {
            var outUsers = new List<string>();
            if (string.IsNullOrEmpty(json)) return outUsers;

            // crude scan for "user":"Name"
            int i = 0;
            while (i >= 0 && i < json.Length)
            {
                int k = json.IndexOf("\"user\"", i, System.StringComparison.Ordinal);
                if (k < 0) break;
                int c = json.IndexOf(':', k);
                if (c < 0) break;
                int q1 = json.IndexOf('"', c + 1);
                if (q1 < 0) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string u = json.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (!string.IsNullOrEmpty(u) && !outUsers.Contains(u)) outUsers.Add(u);
                i = q2 + 1;
            }
            return outUsers;
        }

        private static List<ConfigNode> GetNodesDeep(ConfigNode node, string target)
        {
            var acc = new List<ConfigNode>(64);
            CollectNodesDeep(node, target, acc);
            return acc;
        }

        private static void CollectNodesDeep(ConfigNode node, string target, List<ConfigNode> acc)
        {
            if (node == null) return;
            if (string.Equals(node.name, target, StringComparison.OrdinalIgnoreCase))
                acc.Add(node);

            var children = node.nodes;
            if (children == null) return;
            for (int i = 0; i < children.Count; i++)
                CollectNodesDeep(children[i], target, acc);
        }

        // --- helpers: map SCANsat type bits to readable labels, fallback to numeric ---
        private static void AddScanTypesTo(HashSet<string> acc, string typeStr)
        {
            int n;
            if (!int.TryParse(typeStr, out n))
            {
                acc.Add("type=" + typeStr);
                return;
            }
            if ((n & 1) != 0) acc.Add("AltimetryLo");
            if ((n & 2) != 0) acc.Add("AltimetryHi");
            if ((n & 4) != 0) acc.Add("Biome");
            if ((n & 8) != 0) acc.Add("Anomaly");
            if ((n & 16) != 0) acc.Add("AnomalyDetail");
            if ((n & 32) != 0) acc.Add("Resources");
        }
        private static void AddType(int mask, int bit, string name, List<string> acc)
        {
            if ((mask & bit) != 0) acc.Add(name);
        }

        private static ConfigNode LoadPersistentWithRetry(out string persPath)
        {
            persPath = Path.Combine(
                KSPUtil.ApplicationRootPath,
                "saves",
                HighLogic.SaveFolder ?? "",
                "persistent.sfs");

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var root = ConfigNode.Load(persPath);
                    if (root != null && string.Equals(root.name, "GAME", StringComparison.Ordinal))
                        return root;
                }
                catch { }
                System.Threading.Thread.Sleep(100);
            }
            return null;
        }

        private void DrawSettingsMenu(int windowID)
        {
            GUILayout.BeginVertical();

            // Username
            GUILayout.Label("User Name:");
            GlobalConfig.userName = GUILayout.TextField(GlobalConfig.userName);

            // Server URL
            GUILayout.Label("Server Url:");
            GlobalConfig.ServerUrl = GUILayout.TextField(GlobalConfig.ServerUrl);

            // Seed
            GUILayout.Label("Seed Value:");
            GlobalConfig.seedValue = GUILayout.TextField(GlobalConfig.seedValue);

            GUILayout.Space(8f);

            // ---- Color (Hex) ----
            GUILayout.Label("User Color (Hex, e.g. #00E5FF):");
            if (_colorHexInput == null) // init from config once
                _colorHexInput = GlobalConfig.userColorHex ?? "";

            _colorHexInput = GUILayout.TextField(_colorHexInput, GUILayout.MinWidth(120));

            // Validate/preview
            string testHex = NormalizeHex(_colorHexInput);
            if (string.IsNullOrEmpty(testHex))
            {
                _colorValid = true;           // empty = "auto / not set"
                _parsedColor = Color.white;   // preview as white when empty
            }
            else
            {
                _colorValid = ColorUtility.TryParseHtmlString(testHex, out _parsedColor);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Preview:", GUILayout.Width(60));

            EnsureSwatchTex();

            // reserve a fixed rect for the swatch
            Rect r = GUILayoutUtility.GetRect(40, 20, GUILayout.Width(40), GUILayout.Height(20));

            // draw a simple outline box so the swatch has a border
            GUI.Box(r, GUIContent.none);

            // tint and draw the swatch
            Color prev = GUI.color;
            GUI.color = _colorValid ? _parsedColor : new Color(1f, 0.5f, 0.5f, 1f);
            GUI.DrawTexture(r, _swatchTex, ScaleMode.StretchToFill);
            GUI.color = prev;

            GUILayout.Space(10);
            if (GUILayout.Button("Clear"))
            {
                _colorHexInput = "";
            }
            GUILayout.EndHorizontal();

            // Preset swatches
            GUILayout.Label("Presets:");
            string[] presets = { "#a4a148", "#59a448", "#6b48a4", "#a45948" };
            GUILayout.BeginHorizontal();
            foreach (var hex in presets)
            {
                Color c; ColorUtility.TryParseHtmlString(hex, out c);
                prev = GUI.backgroundColor;
                GUI.backgroundColor = c;
                if (GUILayout.Button(GUIContent.none, GUILayout.Width(24), GUILayout.Height(20)))
                {
                    _colorHexInput = hex;
                    _parsedColor = c;
                    _colorValid = true;
                }
                GUI.backgroundColor = prev;
                GUILayout.Space(4);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);

            // Save / Close
            if (GUILayout.Button("Save Settings"))
            {
                // Persist color (empty = not set)
                var normalized = NormalizeHex(_colorHexInput);
                if (!string.IsNullOrEmpty(normalized) && !_colorValid)
                {
                    ScreenMessages.PostScreenMessage("Invalid color hex, not saved.", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
                else
                {
                    GlobalConfig.userColorHex = string.IsNullOrEmpty(normalized) ? "" : normalized;
                }

                GlobalConfig.SaveConfig();
                ScreenMessages.PostScreenMessage(
                    $"Settings saved! Username: {GlobalConfig.userName}, ServerUrl: {GlobalConfig.ServerUrl}, Seed: {GlobalConfig.seedValue}, Color: {(string.IsNullOrEmpty(GlobalConfig.userColorHex) ? "auto" : GlobalConfig.userColorHex)}",
                    3f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("Settings saved.");
            }

            if (GUILayout.Button("Close"))
            {
                showSettings = false;
                showMenu = true;
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawPresenceWindow(int id)
        {
            GUILayout.BeginVertical();
            presenceScroll = GUILayout.BeginScrollView(presenceScroll, GUILayout.Height(200));

            if (_richLabel == null)
            {
                _richLabel = new GUIStyle(GUI.skin.label) { richText = true };
            }

            var pp = PlayerPresence.Instance;
            if (pp == null || pp.Items == null || pp.Items.Count == 0)
            {
                GUILayout.Label("No players online.");
            }
            else
            {
                foreach (var r in pp.Items)
                {
                    // resolve color
                    Color c = Color.white;
                    if (!string.IsNullOrEmpty(r.color) && ColorUtility.TryParseHtmlString(r.color, out var parsed))
                        c = parsed;

                    string hex = ColorUtility.ToHtmlStringRGBA(c);
                    string userColored = $"<color=#{hex}>{r.user}</color>";

                    GUILayout.Label($"{userColored}  —  {r.scene}  —  {KerbinYMD(r.kspUt)}", _richLabel);
                }
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Close")) showPresence = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
