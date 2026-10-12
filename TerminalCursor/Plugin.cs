using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TerminalCursor
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class TerminalCursorPlugin : BaseUnityPlugin
    {

        public static ManualLogSource mls;

        CursorLockMode locked;
        bool wasVisible = false;

        // Текущий открытый терминал (пока terminalInUse).
        static Terminal currentTerminal;

        // --- состояние кликов ---
        static string lastText;       // последняя строка (сигнатура раскладки)
        static int lastCharCount = -1; // символов в textInfo на прошлый кадр
        static int lastVisLines = -1; // видимых строк на прошлый кадр (typewriter)
        static int hoverStart = -1;  // границы подсвеченного слова под курсором
        static int hoverEnd = -1;
        static readonly List<int> cmdStarts = new List<int>(); // границы всех командных слов
        static readonly List<int> cmdEnds = new List<int>();
        static readonly List<bool> cmdCanonical = new List<bool>(); // заголовки '>X' / "X"

        // Кэш кликабельных ключей текущего узла: строится один раз на узел,
        // чтобы per-frame проверка слов не перебирала все 155 ключей игры.
        static string cachedNodeKey;
        static readonly HashSet<string> clickableKeys = new HashSet<string>();

        // Свой runner-объект: GameObject самого плагина игра уничтожает при загрузке
        // первой сцены, поэтому Update плагина не вызывается. Объект, созданный уже
        // при загруженной сцене (DontDestroyOnLoad + HideAndDontSave), живёт.
        static TerminalCursorRunner runner;

        static void EnsureRunner()
        {
            if (runner != null) return;
            var go = new GameObject("TerminalCursorRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            runner = go.AddComponent<TerminalCursorRunner>();
            mls.LogInfo("[TerminalCursor] runner created in scene '" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "'");
        }

        private void Awake()
        {
            // Plugin startup logic
            mls = base.Logger;


On.Terminal.BeginUsingTerminal += Terminal_BeginUsingTerminal;
            On.Terminal.QuitTerminal += Terminal_QuitTerminal;
            On.Terminal.OnSubmit += Terminal_OnSubmit;
            On.GameNetcodeStuff.PlayerControllerB.KillPlayer += PlayerControllerB_KillPlayer;

            mls.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }



        private void Terminal_BeginUsingTerminal(On.Terminal.orig_BeginUsingTerminal orig, Terminal self)
        {
            // ОТКЛЮЧАЕМ SelectAll() при активации поля.
            // Игра в ActivateInputFieldInternal() -> OnFocus() -> SelectAll()
            // создаёт выделение (stringPosition=text.Length, stringSelectPosition=0),
            // которое рендерится как подсветка символов (GenerateHightlight).
            // KeepCaretAtEnd убирает его в Update(), но LateUpdate() -> SelectAll()
            // пересоздаёт -> цикл "мигания". Отключаем навсегда (persistent на
            // одном экземпляре TMP_InputField).
            if (self != null && self.screenText != null)
            {
                self.screenText.onFocusSelectAll = false;
            }

            orig(self);
            mls.LogWarning("[TerminalCursor] --- Terminal usage started explicitly by system hook. ---");
            locked = Cursor.lockState;
            wasVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (StartOfRound.Instance.localPlayerController != null)
            {
                StartOfRound.Instance.localPlayerController.disableLookInput = true;
            }

currentTerminal = self;
            lastText = null;
            lastCharCount = -1;
            lastVisLines = -1;
            hoverStart = hoverEnd = -1;
            cachedNodeKey = null;
            clickableKeys.Clear();
            cmdStarts.Clear(); cmdEnds.Clear(); cmdCanonical.Clear();
// При повторном входе каретка остаётся с позиции прошлой сессии - на кадр
            // мелькает в случайном месте. Ставим строковую позицию в конец (сеттер
            // поднимает m_IsCaretPositionDirty - каретка уедет в конец в первом же
            // Rebuild) и обновляем label синхронно.
            self.screenText.stringPosition = self.screenText.text.Length;
            self.screenText.ForceLabelUpdate();
            EnsureRunner();
        }
private void Terminal_QuitTerminal(On.Terminal.orig_QuitTerminal orig, Terminal self, bool syncTerminalInUse)
        {
            orig(self, syncTerminalInUse);
            currentTerminal = null;
            hoverStart = hoverEnd = -1;
            Cursor.lockState = locked;
            Cursor.visible = wasVisible;

            if (StartOfRound.Instance.localPlayerController != null)
            {
                StartOfRound.Instance.localPlayerController.disableLookInput = false;
            }
        }

// После ЛЮБОГО сабмита (клик по команде или ручной Enter) текст узла
        // уже заменён, а каретка input-поля остаётся в позиции старого текста -
        // на один кадр она мелькает в случайном месте, пока игра не переактивирует
        // поле. Ставим строковую позицию в конец: сеттер поднимает
        // m_IsCaretPositionDirty, и в первом же Rebuild каретка перескочит в конец
        // (MoveTextEnd не годится - он крутит caret-индексы по устаревшему textInfo).
        private void Terminal_OnSubmit(On.Terminal.orig_OnSubmit orig, Terminal self)
        {
            orig(self);
            if (self == null || self.screenText == null || !self.terminalInUse) return;
            self.screenText.stringPosition = self.screenText.text.Length;
            self.screenText.ForceLabelUpdate();
        }
        private void PlayerControllerB_KillPlayer(On.GameNetcodeStuff.PlayerControllerB.orig_KillPlayer orig, GameNetcodeStuff.PlayerControllerB self, Vector3 bodyVelocity, bool spawnBody, CauseOfDeath causeOfDeath, int deathAnimation, Vector3 positionOffset, bool setOverrideDropItems)
        {
            orig(self, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset, setOverrideDropItems);

            if (!self.IsLocalPlayer) return;

            Cursor.lockState = locked;
            Cursor.visible = wasVisible;
            if (StartOfRound.Instance.localPlayerController != null)
            {
                StartOfRound.Instance.localPlayerController.disableLookInput = false;
            }
        }

// ====================================================================
        //  Per-frame logic (driven by our own runner object)
        // ====================================================================
        internal static void Tick()
        {
            if (currentTerminal == null || !currentTerminal.terminalInUse) return;

            KeepCaretAtEnd(currentTerminal);

            // --- hover / click handling ---
            HandleMouse(currentTerminal);
        }

        // Держим каретку input-поля в КОНЦЕ текста каждый кадр, пока терминал
        // открыт. Иначе после смены узла (LoadNewNode) или повторного входа
        // TMP_InputField оставляет строковую позицию от старого текста: каретка
        // мелькает в случайном месте и портит ввод (буквы вставляются не туда /
        // игнорируются). В ваниле позиция всегда в конце - для кликов и вставки
        // команд это ровно то, что нужно (в т.ч. спидраннерам).
        static void KeepCaretAtEnd(Terminal t)
        {
            TMP_InputField f = t.screenText;
            if (f == null || !f.isFocused) return;
            // stringPosition (getter = stringSelectPositionInternal) и selectionStringFocusPosition
            // (setter = stringSelectPositionInternal) работают в СТРОКОВЫХ индексах.
            // selectionFocusPosition — это CARET-индекс (в textInfo), его нельзя
            // сравнивать с text.Length.
            int len = f.text.Length;
            if (f.stringPosition != len || f.selectionStringFocusPosition != len)
            {
                f.stringPosition = len;
                f.selectionStringFocusPosition = len;
            }
        }

        static Camera GetCam(TMP_Text tc)
        {
            Canvas canvas = tc != null ? tc.canvas : null;
            if (canvas != null && canvas.worldCamera != null) return canvas.worldCamera;
            return Camera.main;
        }

        // Нормализация слова для сравнения с ключевыми словами терминала:
        // оставляем только буквы/цифры и приводим к нижнему регистру. Так снимаются
        // декоративные префиксы терминала ('>MOONS'), кавычки и точки, а также
        // дефисы в названиях (41-Experimentation).
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString().ToLowerInvariant();
        }

    // Слово под указанной экранной точкой - через штатный хит-тест TMP.
        static bool TryGetWordAt(Terminal t, Vector2 screenPos, out string rawWord, out string key, out int start, out int end)
        {
            rawWord = null; key = null; start = -1; end = -1;
            TMP_Text tc = t.screenText != null ? t.screenText.textComponent : null;
            if (tc == null) return false;
            Camera cam = GetCam(tc);
            if (cam == null) return false;

            TMP_TextInfo ti = tc.textInfo;
            int idx = TMP_TextUtilities.FindIntersectingCharacter(tc, screenPos, cam, true);
            if (idx < 0 || idx >= ti.characterCount) return false;
            if (!IsWordChar(ti.characterInfo[idx])) return false;

            int s = idx, e = idx;
            while (s > 0 && IsWordChar(ti.characterInfo[s - 1])) s--;
            while (e < ti.characterCount - 1 && IsWordChar(ti.characterInfo[e + 1])) e++;

            start = s; end = e;
            rawWord = BuildWord(ti, s, e);
            key = Normalize(rawWord);
            return !string.IsNullOrEmpty(key);
        }

        // --- подсветка через цвета вершин TMP ---
        static readonly Color32 CommandColor = new Color32(90, 200, 255, 255);   // канонические (заголовки/кавычки)
        static readonly Color32 DimCommandColor = new Color32(70, 130, 170, 255); // прочие вхождения команд
        static readonly Color32 HoverColor = new Color32(255, 214, 0, 255);      // команда под курсором

        static void SetCharColor(TMP_Text tc, int i, Color32 c)
        {
            TMP_TextInfo ti = tc.textInfo;
            TMP_CharacterInfo ci = ti.characterInfo[i];
            if (!ci.isVisible) return;
            Color32[] arr = ti.meshInfo[ci.materialReferenceIndex].colors32;
            if (arr == null) return;
            int v = ci.vertexIndex;
            arr[v] = c; arr[v + 1] = c; arr[v + 2] = c; arr[v + 3] = c;
        }

    // Собираем границы всех слов текущего текста, которые являются командами.
        // Канонические вхождения: заголовки ">MOONS" и слова в кавычках '"Help"' -
        // их подсвечиваем ярко; остальные вхождения того же слова - приглушённо.
        static void BuildCommandRanges(TMP_Text tc)
        {
            cmdStarts.Clear(); cmdEnds.Clear(); cmdCanonical.Clear();
            TMP_TextInfo ti = tc.textInfo;
            int i = 0;
            while (i < ti.characterCount)
            {
                if (!IsWordChar(ti.characterInfo[i])) { i++; continue; }
                int s = i;
                while (i < ti.characterCount && IsWordChar(ti.characterInfo[i])) i++;
                int e = i - 1;
                string key = Normalize(BuildWord(ti, s, e));
                if (string.IsNullOrEmpty(key) || !clickableKeys.Contains(key)) continue;

                // предшествующий видимый символ: '>' => заголовок; '"' сразу перед словом => кавычки
                char prev = FindPrevVisibleChar(ti, s);
                char next = FindNextVisibleChar(ti, e);
                bool canonical = prev == '>' || prev == '"' || next == '"';
                cmdStarts.Add(s); cmdEnds.Add(e); cmdCanonical.Add(canonical);
            }
        }

        static char FindPrevVisibleChar(TMP_TextInfo ti, int idx)
        {
            for (int i = idx - 1; i >= 0; i--)
            {
                TMP_CharacterInfo ci = ti.characterInfo[i];
                if (!ci.isVisible) continue;
                return ci.character;
            }
            return '\0';
        }

        static char FindNextVisibleChar(TMP_TextInfo ti, int idx)
        {
            for (int i = idx + 1; i < ti.characterCount; i++)
            {
                TMP_CharacterInfo ci = ti.characterInfo[i];
                if (!ci.isVisible) continue;
                return ci.character;
            }
            return '\0';
        }

    static bool InRange(int i, int s, int e) { return i >= s && i <= e; }

    // Раскрашивает командные слова: канонические - ярко, прочие - приглушённо,
        // ховер поверх - HoverColor. Остальные символы возвращаются к исходному цвету.
        static void ApplyHighlights(TMP_Text tc, int hs, int he)
        {
            TMP_TextInfo ti = tc.textInfo;
            for (int i = 0; i < ti.characterCount; i++)
            {
                if (!ti.characterInfo[i].isVisible) continue;
                Color32 c = ti.characterInfo[i].color;
                if (hs >= 0 && InRange(i, hs, he)) c = HoverColor;
                else
                {
                    for (int r = 0; r < cmdStarts.Count; r++)
                        if (InRange(i, cmdStarts[r], cmdEnds[r]))
                        {
                            c = (r < cmdCanonical.Count && cmdCanonical[r]) ? CommandColor : DimCommandColor;
                            break;
                        }
                }
                SetCharColor(tc, i, c);
            }
            tc.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }

// Выполнение команды штатным путём: имитируем ввод слова и жмём OnSubmit.
        static void ExecuteCommand(Terminal t, string key)
        {
            if (t.screenText == null || string.IsNullOrEmpty(key)) return;
            string baseText = t.currentText ?? t.screenText.text;
            string newText = baseText + key;
            // На кадр подстановки гасим поле: каретка не рисуется вообще, а игра
            // в конце OnSubmit сама переактивирует поле уже на новом тексте
            // (ActivateInputField стоит последней строкой в OnSubmit).
            bool wasActive = t.screenText.isFocused;
            if (wasActive) t.screenText.DeactivateInputField();
            t.screenText.text = newText;   // TextChanged сам поднимет textAdded на разницу длин
            // страховка, если onValueChanged не сработал (modifyingText и пр.)
            t.currentText = newText;
            t.textAdded = key.Length;
            t.OnSubmit();
// страховка каретки, если поле не был активно и игра её не переактивировала
            if (!wasActive)
            {
                t.screenText.stringPosition = t.screenText.text.Length;
                t.screenText.ForceLabelUpdate();
            }
            // узел сменился: сбрасываем кэш раскладки, чтобы пересобрать на след. кадре
            lastText = null;
            lastCharCount = -1;
            lastVisLines = -1;
            mls.LogInfo($"[TerminalCursor] click '{key}' -> node '{(t.currentNode != null ? t.currentNode.name : "null")}'");
        }

        // Экранные пиксели -> пространство рендер-буфера камеры (pixelRect).
        // Нужно из-за пониженного/динамического разрешения: Screen 2560x1440, а
        // камера рендерит в 970x580, и WorldToScreenPoint/FindIntersectingCharacter
        // работают именно в этом буфере.
        static Vector2 ToCamSpace(Camera cam, Vector2 screenPos)
        {
            Rect r = cam.pixelRect;
            return new Vector2(r.x + screenPos.x * r.width / Screen.width,
                               r.y + screenPos.y * r.height / Screen.height);
        }

    static void HandleMouse(Terminal t)
        {
            TMP_Text tc = t.screenText != null ? t.screenText.textComponent : null;
            if (tc == null) return;
            Camera cam = GetCam(tc);
            if (cam == null) return;

UpdateClickableCache(t);

            // ВАЖНО: ForceMeshUpdate() из мода НЕ зовём - наша принудительная
            // перестройка расходится с игровой (TMP_InputField вставляет символ
            // каретки, индексы textInfo сдвигаются), от этого уезжали и каретка,
            // и подсветка. textInfo всегда актуальна после перестройки меша игрой:
            // она происходит при любом изменении текста и typewriter-анимации.
            TMP_TextInfo ti = tc.textInfo;
            string txt = tc.text;
            if (txt != lastText || ti.characterCount != lastCharCount || tc.maxVisibleLines != lastVisLines)
            {
                lastText = txt;
                lastCharCount = ti.characterCount;
                lastVisLines = tc.maxVisibleLines;
                BuildCommandRanges(tc);
            }

            Mouse m = Mouse.current;
            int nhs = -1, nhe = -1;
            string clickedKey = null;
            if (m != null)
            {
                Vector2 cp = ToCamSpace(cam, m.position.ReadValue());
                string raw, key; int ws, we;
                if (TryGetWordAt(t, cp, out raw, out key, out ws, out we) && clickableKeys.Contains(key))
                {
                    nhs = ws; nhe = we;
                    if (m.leftButton.wasPressedThisFrame) clickedKey = key;
                }
            }

            // Перекраска каждый кадр (дешёвая): игра затирает цвета вершин своими
            // перестройками меша, а ForceMeshUpdate мы больше не зовём вслепую.
            hoverStart = nhs; hoverEnd = nhe;
            ApplyHighlights(tc, hoverStart, hoverEnd);

            if (clickedKey != null) ExecuteCommand(t, clickedKey);
        }

        // Кэш кликабельных слов: пересобираем при смене узла терминала.
        static void UpdateClickableCache(Terminal t)
        {
            TerminalNode cn = t.currentNode;
            string nodeKey = cn != null ? cn.name : "<null>";
            if (nodeKey == cachedNodeKey) return;
            cachedNodeKey = nodeKey;
            clickableKeys.Clear();

            if (cn != null)
            {
                if (cn.acceptAnything) return; // узел принимает всё - ничего не подсвечиваем
                if (cn.overrideOptions)
                {
                    if (cn.terminalOptions != null)
                        foreach (CompatibleNoun opt in cn.terminalOptions)
                            if (opt != null && opt.noun != null && !string.IsNullOrEmpty(opt.noun.word))
                                clickableKeys.Add(Normalize(opt.noun.word));
                    return;
                }
            }

            TerminalNodesList list = t.terminalNodes;
            if (list == null || list.allKeywords == null) return;
            foreach (TerminalKeyword kw in list.allKeywords)
            {
                if (kw == null || string.IsNullOrEmpty(kw.word) || kw.isVerb) continue;
                // одиночное слово работает, если есть глагол по умолчанию
                // (moons/store/...), готовый узел-ответ (help, scan) или это код объекта
                if (kw.defaultVerb != null || kw.specialKeywordResult != null || kw.accessTerminalObjects)
                    clickableKeys.Add(Normalize(kw.word));
            }
        }

        private static bool IsWordChar(TMP_CharacterInfo ci)
        {
            if (!ci.isVisible) return false;
            return !char.IsWhiteSpace(ci.character);
        }

        private static string BuildWord(TMP_TextInfo ti, int start, int end)
        {
            var sb = new StringBuilder();
            for (int k = start; k <= end; k++) sb.Append(ti.characterInfo[k].character);
            return sb.ToString();
        }
    }


    /// <summary>
    ///   Свой per-frame runner: GameObject самого плагина игра уничтожает при
    ///   загрузке первой сцены, поэтому Update плагина не вызывается. Этот объект
    ///   создаётся уже при загруженной сцене и живёт дальше.
    /// </summary>
    public class TerminalCursorRunner : MonoBehaviour
    {
        private void Update()
        {
            TerminalCursorPlugin.Tick();
        }
    }
}