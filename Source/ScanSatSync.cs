// ScanSync.cs
// KSP 1.12, Unity 2019.4, C# 7.3
// Every 10 s: Save local SCANcontroller → POST → GET → Load.
// No UI. No hashes. No coverage logging.

using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public sealed class ScanSync : MonoBehaviour
    {
        private const string ModuleName = "SCANcontroller";
        private const float IntervalSeconds = 10f;
        private const int HttpTimeoutSec = 6;

        private static bool s_alive;
        private bool _running;

        private void Awake()
        {
            if (s_alive) { Destroy(this); return; }
            s_alive = true;
            DontDestroyOnLoad(gameObject);
            try { GlobalConfig.LoadConfig(); } catch { }
        }

        private void Start()
        {
            _running = true;
            StartCoroutine(Loop());
        }

        private void OnDestroy()
        {
            _running = false;
            s_alive = false;
        }

        private IEnumerator Loop()
        {
            var wait = new WaitForSecondsRealtime(IntervalSeconds);
            while (_running)
            {
                yield return SyncOnce();
                yield return wait;
            }
        }

        private IEnumerator SyncOnce()
        {
            if (!SessionGate.Ready) yield break;
            var module = FindScanController();
            if (module == null) yield break;

            var nodeText = BuildScanNodeText(module);
            if (string.IsNullOrEmpty(nodeText)) yield break;

            // --- read everything from GlobalConfig ---
            string baseUrl = GlobalConfig.ServerUrl;
            string user = GlobalConfig.userName;
            string saveId = GlobalConfig.sharedSaveId;

            // hard guard: skip if any missing
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(saveId))
                yield break;

            string url = baseUrl.TrimEnd('/') + "/scenarios/" + UnityWebRequest.EscapeURL(saveId) + "/" + ModuleName;
            // --- end config block ---


            // POST
            using (var post = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                post.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(nodeText));
                post.downloadHandler = new DownloadHandlerBuffer();
                post.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                post.SetRequestHeader("X-User", user);
                post.timeout = HttpTimeoutSec;

                yield return post.SendWebRequest();

                if (post.isNetworkError || post.isHttpError)
                    yield break;
            }

            // GET
            using (var get = UnityWebRequest.Get(url + "?user=" + UnityWebRequest.EscapeURL(user)))
            {
                get.downloadHandler = new DownloadHandlerBuffer();
                get.timeout = HttpTimeoutSec;

                yield return get.SendWebRequest();

                if (get.isNetworkError || get.isHttpError)
                    yield break;

                string text = get.downloadHandler.text ?? "";
                if (text.Length == 0) yield break;

                var node = ParseConfigFromString(text);
                if (node == null || !string.Equals(node.name, ModuleName, StringComparison.Ordinal))
                    yield break;

                try { module.Load(node); } catch { /* ignore */ }
            }
        }

        private static ScenarioModule FindScanController()
        {
            var scenarios = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.scenarios : null;
            if (scenarios == null) return null;

            for (int i = 0; i < scenarios.Count; i++)
            {
                var ps = scenarios[i];
                if (ps != null && ps.moduleName == ModuleName && ps.moduleRef != null)
                    return ps.moduleRef;
            }
            return null;
        }

        private static string BuildScanNodeText(ScenarioModule module)
        {
            try
            {
                var node = new ConfigNode(ModuleName);
                module.Save(node);
                return node.ToString();
            }
            catch { return null; }
        }

        private static ConfigNode ParseConfigFromString(string text)
        {
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SimpleMultiplayer", "PluginData");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "scansat_inject.tmp");
                File.WriteAllText(file, text, Encoding.UTF8);
                var node = ConfigNode.Load(file);
                try { File.Delete(file); } catch { }
                return node;
            }
            catch { return null; }
        }
    }
}