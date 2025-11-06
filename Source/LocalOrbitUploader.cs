// LocalOrbitUploader.cs — gated by SessionGate.Ready, singleton, Flight only.
// Unity 2019.4, KSP 1.12, C# 7.3

using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class LocalOrbitUploader : MonoBehaviour
    {
        private const float UploadIntervalSeconds = 1f;

        private static bool s_alive;      // singleton guard
        private string _postUrl;          // e.g. http://host/orbits/<saveId>
        private bool _running;

        private void Start()
        {
            // one instance per session
            if (s_alive) { Destroy(this); return; }
            s_alive = true;

            // only run after new/load from main menu
            if (!SessionGate.Ready) { enabled = false; return; }

            // ensure config present
            try { GlobalConfig.LoadConfig(); } catch { }

            var baseUrl = (GlobalConfig.ServerUrl ?? "").TrimEnd('/');
            var saveId = GlobalConfig.sharedSaveId ?? "";
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(saveId))
            {
                Debug.LogWarning("[LocalOrbitUploader] missing ServerUrl/sharedSaveId; disabled");
                enabled = false; return;
            }

            _postUrl = baseUrl + "/orbits/" + Uri.EscapeDataString(saveId);

            _running = true;
            StartCoroutine(UploadLoop());
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);

            Debug.Log($"[LocalOrbitUploader] started, POST {_postUrl} every {UploadIntervalSeconds}s");
        }

        private void OnDestroy()
        {
            _running = false;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            s_alive = false;
        }

        private void OnSceneChange(GameScenes _)
        {
            _running = false; // stop loop on any scene switch
        }

        private IEnumerator UploadLoop()
        {
            var wait = new WaitForSecondsRealtime(UploadIntervalSeconds);
            while (_running)
            {
                if (SessionGate.Ready && HighLogic.LoadedScene == GameScenes.FLIGHT)
                    yield return UploadOnce();
                yield return wait;
            }
        }

        private IEnumerator UploadOnce()
        {
            // basic guards
            var v = FlightGlobals.ActiveVessel;
            if (v == null) yield break;
            var o = v.orbit;
            if (o == null || o.referenceBody == null) yield break;

            double nowUT = Planetarium.GetUniversalTime();
            var inv = CultureInfo.InvariantCulture;

            // CSV: user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT
            string user = (GlobalConfig.userName ?? "Player").Replace(',', ' ').Trim();
            string vessel = (v.vesselName ?? "Vessel").Replace(',', ' ').Trim();
            string body = (o.referenceBody.bodyName ?? "Unknown").Replace(',', ' ').Trim();

            double epochUT = nowUT;
            double sma = o.semiMajorAxis;
            double ecc = o.eccentricity;
            double incDeg = o.inclination;
            double lanDeg = o.LAN;
            double argpDeg = o.argumentOfPeriapsis;
            double mnaRad = o.getMeanAnomalyAtUT(nowUT);

            string colorHex = ResolveUserColor(user);

            double updatedUT = nowUT;

            var sb = new StringBuilder(256);
            sb.Append(user).Append(',')
              .Append(vessel).Append(',')
              .Append(body).Append(',')
              .Append(epochUT.ToString(inv)).Append(',')
              .Append(sma.ToString(inv)).Append(',')
              .Append(ecc.ToString(inv)).Append(',')
              .Append(incDeg.ToString(inv)).Append(',')
              .Append(lanDeg.ToString(inv)).Append(',')
              .Append(argpDeg.ToString(inv)).Append(',')
              .Append(mnaRad.ToString(inv)).Append(',')
              .Append(colorHex).Append(',')
              .Append(updatedUT.ToString(inv));

            var req = new UnityWebRequest(_postUrl, "POST");
            var bodyBytes = Encoding.UTF8.GetBytes(sb.ToString() + "\n");
            req.uploadHandler = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");

            yield return req.SendWebRequest();

            if (req.isNetworkError || req.isHttpError)
                Debug.LogWarning($"[LocalOrbitUploader] POST failed: {req.responseCode} {req.error}");

            req.Dispose();
        }

        private static string ResolveUserColor(string user)
        {
            var raw = (GlobalConfig.userColorHex ?? "").Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                if (raw[0] != '#') raw = "#" + raw;
                if (ColorUtility.TryParseHtmlString(raw, out _))
                    return raw;
            }
            // fallback to hash color
            var c = HashColor(user);
            return "#" + ColorUtility.ToHtmlStringRGB(c);
        }

        // Same hash as viewer (RemoteOrbitSync) so colors line up
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
