using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace TerminalMouse
{
    /// <summary>
    /// Автоматизация тестирования: авто-хост лобби + авто-открытие терминала.
    /// Подключается из Plugin.cs при загрузке плагина.
    /// Последовательность: ждём меню -> StartHost() -> ждём спавн -> открываем Terminal.
    /// </summary>
    public class AutoStart : MonoBehaviour
    {
        private ManualLogSource _log;
        private bool _lobbyHosted;
        private bool _terminalOpened;

        /// <summary>
        /// Создаёт GameObject с компонентом AutoStart. Вызывать из Plugin.Awake.
        /// </summary>
        public static void Attach(ManualLogSource log)
        {
            var go = new GameObject("[TerminalMouse] AutoStart");
            DontDestroyOnLoad(go);
            var comp = go.AddComponent<AutoStart>();
            comp._log = log;
        }

        private void Start()
        {
            StartCoroutine(AutoSequence());
        }

        private IEnumerator AutoSequence()
        {
            _log.LogInfo("[AutoStart] Ожидание главного меню...");

            // Ждём появления GameNetworkManager (главное меню загружено)
            yield return new WaitUntil(() => GameNetworkManager.Instance != null);
            yield return new WaitForSeconds(2f);

            // Хостим лобби (аналог нажатий: down -> enter -> esc -> enter -> enter)
            HostLobby();

            // Ждём появления StartOfRound (лобби загружается)
            yield return new WaitUntil(() => StartOfRound.Instance != null);
            yield return new WaitForSeconds(5f);

            // Ждём спавна локального игрока
            yield return new WaitUntil(
                () => StartOfRound.Instance != null &&
                     StartOfRound.Instance.localPlayerController != null);
            yield return new WaitForSeconds(3f);

            // Открываем терминал
            OpenTerminal();
        }

        private void HostLobby()
        {
            if (_lobbyHosted) return;

            var gnm = GameNetworkManager.Instance;
            if (gnm == null)
            {
                _log.LogWarning("[AutoStart] GameNetworkManager не найден, пропускаю хостинг");
                return;
            }

            // StartHost — приватный метод в текущей сборке, вызываем через рефлексию
            var method = typeof(GameNetworkManager).GetMethod(
                "StartHost",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (method == null)
            {
                _log.LogError("[AutoStart] GameNetworkManager.StartHost не найден через рефлексию");
                return;
            }

            method.Invoke(gnm, null);
            _lobbyHosted = true;
            _log.LogInfo("[AutoStart] Лобби запущено (StartHost)");
        }

        private void OpenTerminal()
        {
            if (_terminalOpened) return;

            var player = StartOfRound.Instance?.localPlayerController;
            if (player == null)
            {
                _log.LogWarning("[AutoStart] Локальный игрок не найден, терминал не открыт");
                return;
            }

            var terminal = FindObjectOfType<Terminal>();
            if (terminal == null)
            {
                _log.LogWarning("[AutoStart] Terminal не найден в сцене");
                return;
            }

            // BeginUsingTerminal — public void в Terminal.
            // Plugin.cs подписан на On.Terminal.BeginUsingTerminal, поэтому хук отработает.
            terminal.BeginUsingTerminal();
            _terminalOpened = true;
            _log.LogInfo("[AutoStart] Терминал открыт");
        }
    }
}
