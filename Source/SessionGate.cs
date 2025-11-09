// SessionGate.cs — enable the mod only after starting or loading a save from the main menu.
using System;
using UnityEngine;
using static GameEvents;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class SessionGate : MonoBehaviour
    {
        public static bool Ready { get; private set; } = false;

        // Notify other systems when a scene becomes GUI-ready and a save is loaded
        public static event Action<GameScenes> SceneReady;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            onLevelWasLoadedGUIReady.Add(OnLevelReady);
            onGameSceneLoadRequested.Add(OnSceneChange);
            onGameStateCreated.Add(OnGameStarted);
            onGameStateLoad.Add(OnGameLoaded);
        }

        void OnDestroy()
        {
            onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            onGameSceneLoadRequested.Remove(OnSceneChange);
            onGameStateCreated.Remove(OnGameStarted);
            onGameStateLoad.Remove(OnGameLoaded);
        }

        private void OnGameStarted(Game game) { Ready = true; }
        private void OnGameLoaded(ConfigNode n) { Ready = true; }

        private void OnLevelReady(GameScenes scene)
        {
            if (!Ready || HighLogic.CurrentGame == null) return;
            // Broadcast. Do not start any ScenarioSync coroutines here.
            var cb = SceneReady; if (cb != null) cb(scene);
        }

        private void OnSceneChange(GameScenes next)
        {
            if (next == GameScenes.MAINMENU) Ready = false;
        }
    }
}