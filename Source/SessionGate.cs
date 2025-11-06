// SessionGate.cs — enable the mod only after starting or loading a save from the main menu.
using UnityEngine;
using static GameEvents;

namespace SimpleMultiplayer
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class SessionGate : MonoBehaviour
    {
        public static bool Ready { get; private set; } = false;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            onLevelWasLoadedGUIReady.Add(OnLevelReady);
            onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnDestroy()
        {
            onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            onGameSceneLoadRequested.Remove(OnSceneChange);
        }

        private void OnLevelReady(GameScenes scene)
        {
            // Ready in any scene after MAINMENU when a Game exists.
            Ready = (scene != GameScenes.MAINMENU) && (HighLogic.CurrentGame != null);
        }

        private void OnSceneChange(GameScenes next)
        {
            if (next == GameScenes.MAINMENU) Ready = false;
        }
    }
}
