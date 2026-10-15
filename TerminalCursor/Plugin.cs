using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
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
        internal static Terminal currentTerminal;

        // --- состояние кликов ---
        static string lastText;       // последняя строка (сигнатура раскладки)
        static int lastCharCount = -1; // символов в textInfo на прошлый кадр
        static int lastVisLines = -1; // видимых строк на прошлый кадр (typewriter)
        static int hoverStart = -1;  // границы подсвеченного слова под курсором
        static int hoverEnd = -1;
        static readonly List<int> cmdStarts = new List<int>(); // границы всех командных слов
        static readonly List<int> cmdEnds = new List<int>();
        static readonly List<bool> cmdCanonical = new List<bool>(); // заголовки '>X' / "X"
        static readonly List<string> cmdKeys = new List<string>(); // сырой ключ для ExecuteCommand

        // Нормализованные → сырые ключи
        static readonly Dictionary<string, string> keyMap = new Dictionary<string, string>();

        // Нормализованные display-имена товаров из текста (текст между "* " и "//").
        // Строится один раз на узел, используется для N-граммного матчинга.
        static readonly Dictionary<string, string> displayKeyMap = new Dictionary<string, string>();

        // Кэш узла: пересборка при смене терминального узла
        static string cachedNodeKey;

        // Динамические команды для страниц switch/ping/flash (target list)
        static readonly List<string> customCmds = new List<string>();
        static TerminalNode lastCustomNode;

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

            // Инициализация config-строк (для переводчиков: RTLC, XUnity и др.)
            var cfg = Config.Bind("Strings", "HeaderSwitch", "Switching radar cam view.",
                "Header text for switch target list page");
            cfgHeaderSwitch = cfg;
            cfg = Config.Bind("Strings", "HeaderPing", "Ping radar booster.",
                "Header text for ping target list page");
            cfgHeaderPing = cfg;
            cfg = Config.Bind("Strings", "HeaderFlash", "Flash radar booster.",
                "Header text for flash target list page");
            cfgHeaderFlash = cfg;
            cfg = Config.Bind("Strings", "MarkCurrent", "(current)",
                "Label for the current radar target");
            cfgMarkCurrent = cfg;
            cfg = Config.Bind("Strings", "MarkRadar", "(radar)",
                "Label for non-player radar targets");
            cfgMarkRadar = cfg;

On.Terminal.BeginUsingTerminal += Terminal_BeginUsingTerminal;
            On.Terminal.QuitTerminal += Terminal_QuitTerminal;
            On.Terminal.OnSubmit += Terminal_OnSubmit;
            On.GameNetcodeStuff.PlayerControllerB.KillPlayer += PlayerControllerB_KillPlayer;

            mls.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            // Патч OnFillVBO - единственная точка, где можно выставить каретку по
            // актуальному textInfo (см. комментарий у Patch_OnFillVBO).
            CaretAccess.Init();
            try
            {
                var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                harmony.PatchAll(typeof(TerminalCursorPlugin).Assembly);
            }
            catch (Exception ex)
            {
                mls.LogError("[TerminalCursor] failed to patch TMP_InputField: " + ex);
            }
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
            cachedNodeKey = null;
            keyMap.Clear(); displayKeyMap.Clear();
            cmdStarts.Clear(); cmdEnds.Clear(); cmdCanonical.Clear(); cmdKeys.Clear();
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

        // ===== ФИКС МИГАНИЯ: гармонично пиним каретку в конец при каждом рендере. =====
        // Вызывается из Harmony-патча Patch_OnFillVBO.Prefix (LatePreRender), когда
        // textInfo гарантированно актуален.
        internal static void PinCaretToEnd(TMP_InputField f)
        {
            if (currentTerminal == null || !currentTerminal.terminalInUse) return;
            if (f == null || !ReferenceEquals(f, currentTerminal.screenText)) return;
            TMP_TextInfo ti = f.textComponent != null ? f.textComponent.textInfo : null;
            if (ti == null) return;

            int cc = ti.characterCount;
            int len = f.text != null ? f.text.Length : 0;
            int caret = cc > 0 ? cc - 1 : 0;

            // Устанавливаем позиции напрямую через рефлексию, минуя сеттеры
            // (которые делают пересчёт "строка <-> textInfo" по устаревшему textInfo).
            CaretAccess.Set(CaretAccess.strPos, f, len);
            CaretAccess.Set(CaretAccess.strSelPos, f, len);
            CaretAccess.Set(CaretAccess.carPos, f, caret);
            CaretAccess.Set(CaretAccess.carSelPos, f, caret);
            // Снимаем флаги пересчёта — не даём OnFillVBO перезаписать позиции
            // из caret-индекса по устаревшему textInfo.
            CaretAccess.SetB(CaretAccess.strDirty, f, false);
            CaretAccess.SetB(CaretAccess.carDirty, f, false);
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
        internal static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString().ToLowerInvariant();
        }

        // Индекс символа под экранной точкой - через штатный хит-тест TMP.
        static int HitCharIndex(TMP_Text tc, Camera cam, Vector2 screenPos)
        {
            int idx = TMP_TextUtilities.FindIntersectingCharacter(tc, screenPos, cam, true);
            if (idx < 0 || idx >= tc.textInfo.characterCount) return -1;
            return idx;
        }

        // Диапазон команды, содержащий указанный символ (N-грамма "Zap gun" и
        // сегмент "TZP" покрывают несколько символов, поэтому ищем по диапазону).
        static int FindRangeAt(int charIdx)
        {
            if (charIdx < 0) return -1;
            for (int r = 0; r < cmdStarts.Count; r++)
                if (charIdx >= cmdStarts[r] && charIdx <= cmdEnds[r]) return r;
            return -1;
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

    // Собираем границы всех командных слов текущего текста.
        //
        // ВАЖНО про парсер игры (Terminal.ParsePlayerSentence):
        //   s = RemovePunctuation(ввод)   - снимает ВСЮ пунктуацию (включая '-'),
        //                                   пробелы СОХРАНЯЮТСЯ, регистр -> lower
        //   CheckForExactSentences(s)     - s == kw.word ДОСЛОВНО
        //   иначе Split(' ') -> ParseWord(каждое слово): точное совпадение,
        //                                   иначе kw.word.StartsWith(префикс)
        // Сырые ключи игры бывают с пробелом ("zap gun", "inverse teleporter",
        // "extension ladder", "loud horn", "weed killer", "belt bag",
        // "spray paint", "pro flashlight", "green suit") и с дефисом
        // ("walkie-talkie"). Значит в ExecuteCommand надо слать СЫРОЙ ключ,
        // а не его нормализацию: "inverseteleporter" игра не находит как
        // точную фразу и скатывается к "teleporter" (другой товар!).
        //
        // Приоритет: для каждой позиции слова берётся САМОЕ ДЛИННОЕ совпадение
        // с ключом (1-3 слова). Ключи игры бывают с пробелом ("zap gun",
        // "inverse teleporter", "extension ladder", "hoarding bugs") и с дефисом
        // ("walkie-talkie"). Кладём ключ в keyMap под НОРМАЛИЗОВАННОЙ формой
        // (пробел удалён: "zap gun" -> "zapgun"), а храним СЫРУЮ - её шлём в
        // парсер. Тогда слова экрана "zap"+"gun" дают "zapgun" -> сырой "zap gun".
        // Дедуп в AddRange не даёт короткому совпадению вытеснить длинное.
        static bool AddRange(int s, int e, string key, bool canonical)
        {
            for (int r = 0; r < cmdStarts.Count; r++)
                if (s <= cmdEnds[r] && e >= cmdStarts[r]) return false;
            cmdStarts.Add(s); cmdEnds.Add(e); cmdCanonical.Add(canonical); cmdKeys.Add(key);
            return true;
        }

        // Parse display-имена товаров из текста терминала.
        // Формат: "* Название товара  //  Price: ¤X" или "* Название  //  Цена: ¤X".
        // Разделитель "//" locale-independent.
        static void ParseDisplayNames(string text)
        {
            displayKeyMap.Clear();
            if (string.IsNullOrEmpty(text)) return;

            // Парсим все строки, содержащие "//" (граница между названием и ценой)
            int pos = 0, len = text.Length;
            while (pos < len)
            {
                int nl = text.IndexOf('\n', pos);
                if (nl < 0) nl = len;
                string line = text.Substring(pos, nl - pos);
                pos = nl + 1;

                int sl = line.IndexOf("//", StringComparison.Ordinal);
                if (sl <= 0) continue;

                string name = line.Substring(0, sl).Trim();
                if (name.Length > 0 && name[0] == '*') name = name.Substring(1).Trim();
                if (name.Length == 0) continue;

                string norm = Normalize(name);
                if (norm.Length < 2) continue;

                // Ищем ключ: сначала точное совпадение, иначе самый длинный ключ-префикс
                string raw;
                if (keyMap.TryGetValue(norm, out raw))
                {
                    displayKeyMap[norm] = raw;
                }
                else
                {
                    string best = null, bestRaw = null;
                    foreach (var kv in keyMap)
                    {
                        if (kv.Key.Length < 3) continue;
                        if (!norm.StartsWith(kv.Key, StringComparison.Ordinal)) continue;
                        if (best == null || kv.Key.Length > best.Length)
                        { best = kv.Key; bestRaw = kv.Value; }
                    }
                    if (best != null) displayKeyMap[norm] = bestRaw;
                }
            }
        }

        static void BuildCommandRanges(TMP_Text tc)
        {
            cmdStarts.Clear(); cmdEnds.Clear(); cmdCanonical.Clear(); cmdKeys.Clear();
            TMP_TextInfo ti = tc.textInfo;
            ParseDisplayNames(tc.text);

            var words = new List<(int s, int e, string key)>();
            int i = 0;
            while (i < ti.characterCount)
            {
                if (!IsWordChar(ti.characterInfo[i])) { i++; continue; }
                int s = i;
                while (i < ti.characterCount && IsWordChar(ti.characterInfo[i])) i++;
                int e = i - 1;
                string key = Normalize(BuildWord(ti, s, e));
                if (key.Length == 0) continue;
                words.Add((s, e, key));
            }

            // 1. Display-имена из текста: "Stun grenade" -> keyMap["stungrenade"]="stun"
            //    "Signal Translator" -> keyMap["signaltranslator"]="signal"
            //    "Sofa chair" -> keyMap["sofachair"]="sofa"
            //    Показываем ВЕСЬ товар целиком, а не только ключевое слово.
            for (int w = 0; w < words.Count; w++)
            {
                var sb = new StringBuilder();
                int bestEnd = -1; string bestKey = null;
                for (int k = w; k < words.Count && k <= w + 3; k++)
                {
                    if (k > w && ti.characterInfo[words[k - 1].e].lineNumber != ti.characterInfo[words[k].s].lineNumber) break;
                    sb.Append(words[k].key);
                    string norm = sb.ToString();
                    if (!displayKeyMap.TryGetValue(norm, out var raw)) continue;
                    bestEnd = words[k].e; bestKey = raw;
                }
                if (bestKey == null) continue;
                char prev = FindPrevVisibleChar(ti, words[w].s);
                AddRange(words[w].s, bestEnd, bestKey, prev == '>' || prev == '"');
            }

            // 2. N-граммы 1-4 слова: "Zap gun" -> keyMap["zapgun"]="zap gun"
            //    "Inverse Teleporter" -> keyMap["inverseteleporter"]="inverse teleporter"
            //    "switch Player 1" -> keyMap["switchplayer1"]="switch Player 1"
            for (int w = 0; w < words.Count; w++)
            {
                var sb = new StringBuilder();
                int bestEnd = -1; string bestKey = null;
                for (int k = w; k < words.Count && k <= w + 3; k++)
                {
                    if (k > w && ti.characterInfo[words[k - 1].e].lineNumber != ti.characterInfo[words[k].s].lineNumber) break;
                    sb.Append(words[k].key);
                    string raw;
                    if (!keyMap.TryGetValue(sb.ToString(), out raw)) continue;
                    bestEnd = words[k].e; bestKey = raw;
                }
                if (bestKey == null) continue;
                char prev = FindPrevVisibleChar(ti, words[w].s);
                AddRange(words[w].s, bestEnd, bestKey, prev == '>' || prev == '"');
            }

            // 3. Дефисный токен целиком: "TZP-Inhalant" -> сегмент "tzp" -> ключ "tzp",
            //    но подсвечиваем ВЕСЬ токен (пользователь кликает по любой букве).
            for (int w = 0; w < words.Count; w++)
            {
                string text = BuildWord(ti, words[w].s, words[w].e);
                if (text.IndexOf('-') < 0) continue;

                string bestRaw = null;
                int segStart = words[w].s;
                for (int c = 0; c <= text.Length; c++)
                {
                    if (c != text.Length && text[c] != '-') continue;
                    int segEnd = words[w].s + c - 1;
                    if (segEnd >= segStart)
                    {
                        string segKey = Normalize(BuildWord(ti, segStart, segEnd));
                        string raw;
                        if (segKey.Length > 0 && keyMap.TryGetValue(segKey, out raw) &&
                            (bestRaw == null || segKey.Length > bestRaw.Length)) bestRaw = raw;
                    }
                    segStart = words[w].s + c + 1;
                }
                if (bestRaw == null) continue;
                char prev = FindPrevVisibleChar(ti, words[w].s);
                AddRange(words[w].s, words[w].e, bestRaw, prev == '>' || prev == '"');
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
            string newText;
            // На кастомных узлах (TC_) заменяем весь текст командой, не добавляем.
            // Иначе клик на "> switch Player 1" даст "...switch Player 1switch Player 1"
            if (t.currentNode != null && t.currentNode.name.StartsWith("TC_"))
            {
                newText = key;
                // TerminalCursorPlugin.mls.LogInfo($"[EXEC] TC_ node: replacing text with '{key}'");

                // ВАЖНО: устанавливаем modifyingText = true, чтобы TextChanged игры
                // не восстановил старый текст узла (он делает screenText.text = currentText
                // когда textAdded < 0, а замена 150→15 символов даёт отрицательную разницу).
                var modField = t.GetType().GetField("modifyingText",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (modField != null) modField.SetValue(t, true);
            }
            else
            {
                string baseText = t.currentText ?? t.screenText.text;
                newText = baseText + key;
            }
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
                int r = FindRangeAt(HitCharIndex(tc, cam, cp));
                if (r >= 0)
                {
                    nhs = cmdStarts[r]; nhe = cmdEnds[r];
                    if (m.leftButton.wasPressedThisFrame) clickedKey = cmdKeys[r];
                }
            }

            // Перекраска каждый кадр (дешёвая): игра затирает цвета вершин своими
            // перестройками меша, а ForceMeshUpdate мы больше не зовём вслепую.
            hoverStart = nhs; hoverEnd = nhe;
            ApplyHighlights(tc, hoverStart, hoverEnd);

            if (clickedKey != null) ExecuteCommand(t, clickedKey);
        }

        // Кэш ключей: пересобираем при смене узла терминала.
        static void UpdateClickableCache(Terminal t)
        {
            TerminalNode cn = t.currentNode;
            string nodeKey = cn != null ? cn.name : "<null>";
            if (nodeKey == cachedNodeKey) return;
            cachedNodeKey = nodeKey;
            keyMap.Clear();

            // Кастомные узлы (TC_switch, TC_ping, TC_flash) — берём команды из customCmds
            if (nodeKey != null && nodeKey.StartsWith("TC_"))
            {
                foreach (var cmd in customCmds) AddKey(cmd);
                return;
            }

            if (cn != null)
            {
                if (cn.acceptAnything) return; // узел принимает всё - ничего не подсвечиваем
                if (cn.overrideOptions)
                {
                    if (cn.terminalOptions != null)
                        foreach (CompatibleNoun opt in cn.terminalOptions)
                            if (opt != null && opt.noun != null && !string.IsNullOrEmpty(opt.noun.word))
                                AddKey(opt.noun.word);
                    return;
                }
            }

            TerminalNodesList list = t.terminalNodes;
            if (list == null || list.allKeywords == null) return;
            foreach (TerminalKeyword kw in list.allKeywords)
            {
                if (kw == null || string.IsNullOrEmpty(kw.word)) continue;
                if (kw.isVerb)
                {
                    // verb+noun пары: "view monitor", "inside cam" и т.д.
                    if (kw.compatibleNouns != null)
                        foreach (var c in kw.compatibleNouns)
                            if (c != null && c.noun != null && c.result != null && !string.IsNullOrEmpty(c.noun.word))
                                AddKey(kw.word + " " + c.noun.word);
                    continue;
                }
                // одиночное слово работает, если есть глагол по умолчанию
                // (moons/store/...), готовый узел-ответ (help, scan) или это код объекта
                if (kw.defaultVerb != null || kw.specialKeywordResult != null || kw.accessTerminalObjects)
                    AddKey(kw.word);
            }

            // Добавляем ping/flash как кликабельные (их нет в allKeywords, но они есть
            // в хелпе страницы "Other commands" как команды с аргументами).
            AddKey("ping");
            AddKey("flash");
        }

        // Ключ кладём под нормализованной формой (для сверки со словами экрана),
        // но храним СЫРОЕ kw.word - его отправляем в парсер игры.
        static void AddKey(string word)
        {
            string norm = Normalize(word);
            if (norm.Length == 0) return;
            string old;
            if (keyMap.TryGetValue(norm, out old))
            {
                // при коллизии нормализаций ("zap gun"/"zapgun") предпочтительнее
                // тот ключ, что парсится точной фразой, - с пробелом
                if (word.IndexOf(' ') < 0 && old.IndexOf(' ') >= 0) return;
            }
            keyMap[norm] = word;
        }

        private static bool IsWordChar(TMP_CharacterInfo ci)
        {
            if (!ci.isVisible) return false;
            return !char.IsWhiteSpace(ci.character);
        }

        // ====================================================================
        //  Config-based translatable strings (RTLС-compatible).
        //  RTLC (XUnity.AutoTranslator) translates via a redirect table.
        //  Our dynamic text can't match the table directly, but these config
        //  strings let the RTLC team (or users) provide Russian translations.
        //  Any translator mod (RTLC, XUnity, etc.) reads .cfg files and
        //  overwrites ConfigEntry values — no API needed.
        // ====================================================================
        static ConfigEntry<string> cfgHeaderSwitch, cfgHeaderPing, cfgHeaderFlash;
        static ConfigEntry<string> cfgMarkCurrent, cfgMarkRadar;

        // Кэш reflection-полей для TransformAndName
        static FieldInfo fiTaName, fiTaIsNonPlayer, fiTaTransform;

        static void InitTaFields(object first)
        {
            if (fiTaName == null)
            {
                var t = first.GetType();
                fiTaName = t.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                fiTaIsNonPlayer = t.GetField("isNonPlayer", BindingFlags.Public | BindingFlags.Instance);
                fiTaTransform = t.GetField("transform", BindingFlags.Public | BindingFlags.Instance);
            }
        }

        // Получить отфильтрованный список целей для команды.
        // Возвращает массив (idx, name, isNonPlayer) для совместимости.
        // switch: connected players + radars.
        // ping/flash: только radars (isNonPlayer).
        internal static (int idx, string name, bool isNonPlayer)[] GetValidTargets(string cmd)
        {
            var list = new List<(int idx, string name, bool isNonPlayer)>();
            var map = StartOfRound.Instance?.mapScreen;
            if (map == null) return Array.Empty<(int, string, bool)>();

            var targetsField = map.GetType().GetField("radarTargets",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var radarTargets = targetsField?.GetValue(map) as System.Collections.IList;
            if (radarTargets == null || radarTargets.Count == 0) return Array.Empty<(int, string, bool)>();

            // Кэшируем reflection-поля первого элемента
            InitTaFields(radarTargets[0]);

            // Карта: thisPlayerBody → PlayerControllerB (только подключённые)
            var connectedBodies = new Dictionary<Transform, PlayerControllerB>();
            var allPlayers = StartOfRound.Instance?.allPlayerScripts;
            if (allPlayers != null)
            {
                foreach (var p in allPlayers)
                {
                    if (p != null && p.isPlayerControlled && p.thisPlayerBody != null)
                        connectedBodies[p.thisPlayerBody] = p;
                }
            }

            for (int i = 0; i < radarTargets.Count; i++)
            {
                var target = radarTargets[i];
                string rawName = fiTaName?.GetValue(target) as string ?? "";
                if (string.IsNullOrEmpty(rawName)) continue;
                // Убираем '#' (RemovePunctuation игры его снимает, но # ломает textAdded)
                string name = rawName.Replace("#", "").Trim();
                if (string.IsNullOrEmpty(name)) continue;

                bool isNonPlayer = (bool)(fiTaIsNonPlayer?.GetValue(target) ?? false);
                var targetTransform = fiTaTransform?.GetValue(target) as Transform;

                // Для switch: пропускаем пустые слоты (не подключённые игроки)
                if (!isNonPlayer && !connectedBodies.ContainsKey(targetTransform))
                    continue;

                // Для ping/flash: только радары (isNonPlayer)
                if ((cmd == "ping" || cmd == "flash") && !isNonPlayer)
                    continue;

                list.Add((i, name, isNonPlayer));
            }

            return list.ToArray();
        }

        // Построение кастомного узла со списком целей для switch/ping/flash.
        // Игра использует CheckForPlayerNameCommand(array[0], array[1]) —
        // array[1] матчится по имени GameObject цели (radarTargets[i].name).
        //
        // ПУНКТ 5: перехватываем ТОЛЬКО если >=2 целей.
        // 0 или 1 цель → возвращаем null, чтобы игра обработала ванильно:
        //   - switch 0/1 цель → ванильная страница "Switching radar cam view."
        //   - ping/flash без радаров → ванильная страница товара "Radar-booster"
        internal static TerminalNode BuildTargetListNode(Terminal t, string cmd)
        {
            // Уничтожаем старый кастомный нод (ScriptableObject утекает)
            if (lastCustomNode != null) UnityEngine.Object.Destroy(lastCustomNode);
            lastCustomNode = null;

            var entries = GetValidTargets(cmd);

            // ping/flash без радаров → показать страницу товара Radar-booster
            // (buy radar -> compatibleNouns.defaultVerb -> result)
            if (entries.Length == 0 && (cmd == "ping" || cmd == "flash"))
                return FindItemNodeByKeyword(t, "radar");

            // 1 цель → не перехватывать, пусть игра сделает ванильно
            if (entries.Length < 2)
                return null;

            // >=2 цели → строим список
            var sb = new StringBuilder();
            // ПУНКТ 1: берём заголовок ИЗ ИГРЫ — RTLC/XUnity уже знает эти строки
            // и переведёт их автоматически при рендере (intercepts TMP_Text.text setter).
            // specialNodes[20] = switch success node, [21] = ping, [23] = flash
            string header = null;
            var specialNodes = t.terminalNodes?.specialNodes;
            if (cmd == "switch" && specialNodes != null && specialNodes.Count > 20)
                header = specialNodes[20].displayText;
            else if (cmd == "ping" && specialNodes != null && specialNodes.Count > 21)
                header = specialNodes[21].displayText;
            else if (cmd == "flash" && specialNodes != null && specialNodes.Count > 23)
                header = specialNodes[23].displayText;
            if (string.IsNullOrEmpty(header))
                header = cmd == "switch" ? "Switching radar cam view."
                      : cmd == "ping"  ? "Ping radar booster."
                      : "Flash radar booster.";
            sb.AppendLine(header);
            sb.AppendLine();

            customCmds.Clear();
            int curIdx = -1;
            var map = StartOfRound.Instance?.mapScreen;
            var targetIdxField = map?.GetType().GetField("targetTransformIndex",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetIdxField != null)
                curIdx = (int)targetIdxField.GetValue(map);

            for (int vi = 0; vi < entries.Length; vi++)
            {
                var e = entries[vi];
                // Команда БЕЗ маркеров — для кликабельности (parser expects "switch Player 0")
                string rawLine = cmd + " " + e.name;
                customCmds.Add(rawLine);

                sb.Append("> ").Append(rawLine);

                // ПУНКТ 3: пометка (radar) только для switch (ping/flash и так только радары)
                if (cmd == "switch" && e.isNonPlayer)
                    sb.Append("  " + cfgMarkRadar.Value);

                // Текущая цель помечена (current)
                if (e.idx == curIdx)
                    sb.Append("  " + cfgMarkCurrent.Value);

                sb.AppendLine();
            }

            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.name = "TC_" + cmd;
            node.displayText = sb.ToString();
            node.clearPreviousText = true;
            lastCustomNode = node;
            cachedNodeKey = null;
            return node;
        }

        // Найти страницу товара по keyword-слову (например "radar" -> Radar-booster).
        // Алгоритм игры: keyword(word) -> defaultVerb (buy) -> compatibleNouns
        // где noun == keyword -> result (страница товара с buyItemIndex).
        internal static TerminalNode FindItemNodeByKeyword(Terminal t, string keywordWord)
        {
            var list = t.terminalNodes;
            if (list?.allKeywords == null) return null;

            TerminalKeyword noun = null;
            TerminalKeyword buyVerb = null;
            foreach (var kw in list.allKeywords)
            {
                if (kw == null || string.IsNullOrEmpty(kw.word)) continue;
                if (kw.word == keywordWord) noun = kw;
                else if (kw.word == "buy" && kw.isVerb) buyVerb = kw;
            }
            if (noun == null) return null;
            if (buyVerb == null) buyVerb = noun.defaultVerb;
            if (buyVerb?.compatibleNouns == null) return null;

            foreach (var cn in buyVerb.compatibleNouns)
                if (cn != null && cn.noun == noun && cn.result != null)
                    return cn.result;
            return null;
        }

        // Выполнение команды switch/ping/flash с аргументом (цель по индексу).
        // Вызывается из Harmony-патча ParsePlayerSentence или BuildTargetListNodeInternal.
        internal static TerminalNode ExecuteTargetCommandWithIndex(Terminal t, string cmd, int targetIdx)
        {
            var map = StartOfRound.Instance?.mapScreen;
            if (map == null) return null;

            // Вызываем метод MapScreen напрямую
            string methodName = cmd == "switch" ? "SwitchRadarTargetAndSync"
                              : cmd == "ping"  ? "PingRadarBooster"
                              : "FlashRadarBooster";
            try
            {
                map.GetType().GetMethod(methodName)?.Invoke(map, new object[] { targetIdx });
            }
            catch (System.Exception ex)
            {
                TerminalCursorPlugin.mls.LogWarning($"[TC] {methodName} failed: {ex.Message}");
            }

            // Возвращаем game-специальную ноду (сообщение об успехе)
            int specialIdx = cmd == "switch" ? 20 : cmd == "ping" ? 21 : 23;
            return t.terminalNodes?.specialNodes != null && t.terminalNodes.specialNodes.Count > specialIdx
                ? t.terminalNodes.specialNodes[specialIdx] : null;
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

    // =====================================================================
    //  Доступ к приватным полям TMP_InputField, отвечающим за каретку и
    //  выделение. Нужен потому, что публичные сеттеры (caretPosition /
    //  stringPosition) пересчитывают "строковый индекс <-> индекс в textInfo"
    //  через textInfo, который в момент вызова может быть устаревшим.
    // =====================================================================
    internal static class CaretAccess
    {
        internal static FieldInfo
            strPos, strSelPos,  // m_StringPosition / m_StringSelectPosition (индекс в строке)
            carPos, carSelPos,  // m_CaretPosition / m_CaretSelectPosition (индекс в textInfo)
            strDirty, carDirty; // флаги пересчёта позиций

        internal static void Init()
        {
            var t = typeof(TMP_InputField);
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            strPos = t.GetField("m_StringPosition", F);
            strSelPos = t.GetField("m_StringSelectPosition", F);
            carPos = t.GetField("m_CaretPosition", F);
            carSelPos = t.GetField("m_CaretSelectPosition", F);
            strDirty = t.GetField("m_IsStringPositionDirty", F);
            carDirty = t.GetField("m_IsCaretPositionDirty", F);
        }

        internal static void Set(FieldInfo f, TMP_InputField x, int v) { if (f != null) f.SetValue(x, v); }
        internal static void SetB(FieldInfo f, TMP_InputField x, bool v) { if (f != null) f.SetValue(x, v); }
    }

    // =====================================================================
    //  Harmony-патч на ParsePlayerSentence — перехватывает одиночные слова
    //  switch/ping/flash и возвращает кастомный узел со списком целей.
    //  Это нужно, потому что:
    //  - "switch" без аргумента → specialKeywordResult → статичная страница
    //    "Switching radar cam view." без списка целей;
    //  - "ping" без аргумента → specialNodes[10] → "[There was no action
    //    supplied with the word.]";
    //  - "flash" без аргумента → ParseWord("flash") → prefix-match →
    //    "flashlight" → страница покупки фонарика.
    //  Мод возвращает динамический узел со списком radarTargets (имена игроков
    //  и радаров) в формате "> switch Player 1", где каждая строка кликабельна.
    // =====================================================================
    [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
    internal static class Patch_ParsePlayerSentence
    {
        static MethodInfo removePunct;

        static bool Prefix(Terminal __instance, ref TerminalNode __result)
        {
            // Не трогаем узлы с overrideOptions/acceptAnything (confirm/deny и т.д.)
            if (__instance.currentNode != null &&
                (__instance.currentNode.overrideOptions || __instance.currentNode.acceptAnything))
                return true;

            if (__instance.screenText == null || __instance.textAdded <= 0) return true;

            string s = __instance.screenText.text.Substring(
                Mathf.Max(0, __instance.screenText.text.Length - __instance.textAdded));

            // Вызываем RemovePunctuation игры (приватный метод)
            if (removePunct == null)
                removePunct = AccessTools.Method(typeof(Terminal), "RemovePunctuation");
            if (removePunct != null)
                s = (string)removePunct.Invoke(__instance, new object[] { s });

            // DEBUG: лог состояния
            // TerminalCursorPlugin.mls.LogInfo($"[PREFIX] textLen={__instance.screenText.text.Length} textAdded={__instance.textAdded} s=\"{s}\"");

            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 1)
            {
                // Команда с аргументом (например "switch player 1")
                // Пропускаем сломанный CheckForPlayerNameCommand (матчит только array[1])
                // и вызываем MapScreen-методы напрямую
                string cmdArg = parts[0].ToLowerInvariant();
                if (cmdArg == "switch" || cmdArg == "ping" || cmdArg == "flash")
                {
                    // Собираем аргумент: всё после команды
                    string arg = s.Substring(cmdArg.Length).Trim();
                    // Ищем индекс цели по аргументу
                    var entries = TerminalCursorPlugin.GetValidTargets(cmdArg);
                    int foundIdx = -1;
                    foreach (var e in entries)
                    {
                        if (TerminalCursorPlugin.Normalize(e.name.Replace("#", "")) == TerminalCursorPlugin.Normalize(arg))
                        {
                            foundIdx = e.idx;
                            break;
                        }
                    }
                    if (foundIdx >= 0)
                    {
                        __result = TerminalCursorPlugin.ExecuteTargetCommandWithIndex(__instance, cmdArg, foundIdx);
                        return false;
                    }
                    // Если не нашли цель — пусть игра вернёт ParserError
                }
                return true;
            }

            string cmd = parts[0].ToLowerInvariant();
            if (cmd != "switch" && cmd != "ping" && cmd != "flash") return true;

            __result = TerminalCursorPlugin.BuildTargetListNode(__instance, cmd);
            // null (0 или 1 цель) → пусть игра обработает ванильно
            return __result == null;
        }
    }

    // =====================================================================
    //  ФИКС МИГАНИЯ КАРЕТКИ.
    //
    //  Terminal.Update каждый кадр выполняет screenText.caretPosition =
    //  text.Length, хотя caretPosition - это ИНДЕКС В textInfo, а не позиция в
    //  строке. Сеттер клампит полученное число до textInfo.characterCount - 1 и
    //  поднимает m_IsStringPositionDirty. В кадре, где текст узла уже заменён,
    //  а меш TMP ещё не перестроен, characterCount относится к ПРЕЖНЕЙ
    //  раскладке - кламп даёт середину нового текста, и OnFillVBO пересчитывает
    //  из этого caret-индекса строковую позицию, затирая позицию конца. Итог:
    //  один кадр каретка рисуется в случайном месте текста (в этот же кадр
    //  ломается ввод букв).
    //
    //  Замер подтвердил (cc = textInfo.characterCount, cp = m_CaretPosition):
    //      fill cc=157 sp=268 cp=156   -> текст уже новый (268), раскладка старая
    //      fill cc=269 sp=156 cp=156   -> раскладка новая, каретка в середине
    //  после фикса:
    //      fill cc=157 sp=268 cp=156
    //      fill cc=269 sp=268 cp=268   -> cp == cc-1, каретка в конце
    //
    //  OnFillVBO - единственная точка, где textInfo гарантированно актуален:
    //  TMP_Text перестраивает меш на CanvasUpdate.PreRender, а геометрия
    //  каретки строится позже, на LatePreRender. Поэтому ставим позиции конца
    //  здесь и снимаем флаги пересчёта, чтобы они не были перезатёрты.
    //
    //  Ограничение каретки концом совпадает с ванильным поведением:
    //  Terminal.Update и так каждый кадр возвращает её в конец, так что ручное
    //  позиционирование каретки в терминале не работает и без нас. 
    // =====================================================================
    [HarmonyPatch(typeof(TMP_InputField), "OnFillVBO")]
    internal static class Patch_OnFillVBO
    {
        static void Prefix(TMP_InputField __instance) => TerminalCursorPlugin.PinCaretToEnd(__instance);
    }
}