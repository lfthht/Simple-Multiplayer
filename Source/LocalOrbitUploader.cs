// LocalOrbitUploader.cs  (Flight scene)
// Uploads the active vessel's orbit every 5 seconds to /orbits/<saveId> as one CSV line.
// Matches server.py format and RemoteOrbitSync.cs expectations.
//
// Unity 2019.4.18f1, KSP 1.12, C# 7.3

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

        private string _postUrl;    // e.g. http://localhost:5000/orbits/default
        private bool _running;

        private void Start()
        {
            DontDestroyOnLoad(gameObject);

            var saveId = Uri.EscapeDataString(GlobalConfig.sharedSaveId ?? "default");
            _postUrl = $"{GlobalConfig.ServerUrl}/orbits/{saveId}";

            _running = true;
            StartCoroutine(UploadLoop());
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);

            Debug.Log($"[LocalOrbitUploader] started, POST {_postUrl} every {UploadIntervalSeconds}s");
        }

        private void OnDestroy()
        {
            _running = false;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
        }

        private void OnSceneChange(GameScenes _) => _running = false;

        private IEnumerator UploadLoop()
        {
            var wait = new WaitForSecondsRealtime(UploadIntervalSeconds);
            while (_running)
            {
                yield return UploadOnce();
                yield return wait;
            }
        }

        private IEnumerator UploadOnce()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null) yield break;

            var o = v.orbit;
            if (o == null || o.referenceBody == null) yield break;

            // Build CSV that matches server.py + RemoteOrbitSync.cs
            // user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT
            double nowUT = Planetarium.GetUniversalTime();
            var inv = CultureInfo.InvariantCulture;

            string user = (GlobalConfig.userName ?? "Player").Replace(',', ' ').Trim();
            string vessel = (v.vesselName ?? "Vessel").Replace(',', ' ').Trim();
            string body = (o.referenceBody.bodyName ?? "Unknown").Replace(',', ' ').Trim();

            double epochUT = nowUT;
            double sma = o.semiMajorAxis;           // meters
            double ecc = o.eccentricity;
            double incDeg = o.inclination;             // degrees
            double lanDeg = o.LAN;                     // degrees
            double argpDeg = o.argumentOfPeriapsis;     // degrees
            double mnaRad = o.getMeanAnomalyAtUT(nowUT); // RADIANS (viewer converts to deg)

            // Deterministic per-user color, encoded as #RRGGBB
            Color col = HashColor(user);
            string colorHex = "#" + ColorUtility.ToHtmlStringRGB(col);

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

            string line = sb.ToString();

            var req = new UnityWebRequest(_postUrl, "POST");
            var bodyBytes = Encoding.UTF8.GetBytes(line + "\n");
            req.uploadHandler = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");

            yield return req.SendWebRequest();

            // Unity 2019.4 API (avoid .result)
            if (req.isNetworkError || req.isHttpError)
            {
                Debug.LogWarning($"[LocalOrbitUploader] POST failed: {req.responseCode} {req.error}");
            }
            // else: OK

            req.Dispose();
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
