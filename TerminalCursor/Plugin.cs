using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.IO;

using System.Reflection;
using System.Text;
using System.Globalization;
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
        static int lastVisibleCount = -1; // видимых символов на прошлый кадр
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
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            runner = go.AddComponent<TerminalCursorRunner>();
            mls.LogInfo("[TerminalCursor] runner created in scene '" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "'");
        }

        private void Awake()
        {
            // Plugin startup logic
            mls = base.Logger;

            // Инициализация config-строк (для переводчиков: RTLC, XUnity и др.)
            var cfgHeaderSwitch = Config.Bind("Strings", "HeaderSwitch", "Switching radar cam view.",
                "Header text for switch target list page");
            var cfgHeaderPing = Config.Bind("Strings", "HeaderPing", "Ping radar booster.",
                "Header text for ping target list page");
            var cfgHeaderFlash = Config.Bind("Strings", "HeaderFlash", "Flash radar booster.",
                "Header text for flash target list page");
            var cfgMarkCurrent = Config.Bind("Strings", "MarkCurrent", "(current)",
                "Label for the current radar target");
            var cfgMarkRadar = Config.Bind("Strings", "MarkRadar", "(radar)",
                "Label for non-player radar targets");
            InitHistory(Config);

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
            currentFolder = "all";
            armedFolder = null;
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
            // Пишем ДО orig: после парсинга игра обнуляет textAdded и заменяет
            // текст узла - срезанная строка ввода потерялась бы. Так в историю
            // попадают и неверные команды (пользователь их тоже вводил).
            if (self != null && self.terminalInUse) RecordInput(self);
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

            // На своих страницах (TC_history/TC_bans) текст генерируем сами, и
            // ключи известны точно (customCmds). Матчим их прямым поиском подстроки
            // в тексте: перенос строки (wrap) TMP не мешает, фза "ban <cmd>"
            // подсвечивается целиком даже разорванная на две визуальные строки.
            if (onCustomNode)
            {
                BuildCustomNodeRanges(tc);
                return;
            }

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

        // Подсветка на страницах мода (TC_*): текст страницы генерируем сами,
        // кликабельные строки known точно (customCmds). Ищем каждую команду как
        // подстроку в тексте и проверяем границы слова (соседи не буква/цифра) —
        // поэтому "ban" внутри "banned" не матчится, а "ban <cmd>" подсвечивается
        // целиком даже когда TMP перенёс строку (у подстроки нет понятия строки).
        static void BuildCustomNodeRanges(TMP_Text tc)
        {
            string text = tc.text;
            if (string.IsNullOrEmpty(text)) return;

            var keys = new List<string>(customCmds);
            keys.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });

            var claimed = new bool[text.Length];
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                int from = 0, idx;
                while ((idx = text.IndexOf(key, from, StringComparison.Ordinal)) >= 0)
                {
                    from = idx + 1;
                    int end = idx + key.Length - 1;
                    if (claimed[idx]) continue;
                    char before = idx > 0 ? text[idx - 1] : ' ';
                    char after = end + 1 < text.Length ? text[end + 1] : ' ';
                    if (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after)) continue;
                    bool busy = false;
                    for (int c = idx; c <= end; c++) if (claimed[c]) { busy = true; break; }
                    if (busy) continue;
                    for (int c = idx; c <= end; c++) claimed[c] = true;
                    cmdStarts.Add(idx); cmdEnds.Add(end);
                    cmdCanonical.Add(true); cmdKeys.Add(key);
                }
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
        // Для команд, требующих аргумент (info, view, TRANSMIT и др.), вместо
        // выполнения просто вводим команду с пробелом в конец текста — пользователь
        // дописывает аргумент и нажимает Enter сам.
        static void ExecuteCommand(Terminal t, string key)
        {
            if (t.screenText == null || string.IsNullOrEmpty(key)) return;

            // Если армим папку (после "page <имя> $") — добавляем команду в папку.
            // Но команды управления (sort, page, del, ban, unban, history) НЕ
            // поглощаются — они должны работать как обычно.
            if (!string.IsNullOrEmpty(armedFolder))
            {
                string armKey = Normalize(key);
                int sp = key.IndexOf(' ');
                string firstWord = sp < 0 ? armKey : Normalize(key.Substring(0, sp));
                // Команды управления не поглощаются армом.
                if (firstWord != "sort" && firstWord != "page" &&
                    firstWord != "del" && firstWord != "delete" &&
                    firstWord != "rem" && firstWord != "remove" &&
                    firstWord != "ban" && firstWord != "unban" &&
                    firstWord != HistoryWord)
                {
                    if (armKey.Length > 0 && !folders[armedFolder].Contains(armKey))
                    {
                        folders[armedFolder].Add(armKey);
                        SaveData();
                        mls.LogInfo("[TerminalCursor] added '" + armKey + "' to folder '" + armedFolder + "'");
                    }
                    armedFolder = null;
                    // Возвращаем обновлённую страницу.
                    BuildHistoryNode(t);
                    t.screenText.ActivateInputField();
                    t.screenText.stringPosition = t.screenText.text.Length;
                    t.screenText.selectionStringFocusPosition = t.screenText.text.Length;
                    t.screenText.ForceLabelUpdate();
                    return;
                }
                // Команда управления — пропускаем дальше (не поглощаем).
            }

            // Клик по "page" на TC_ странице — подставляем "page " в конец,
            // не стирая текст страницы.
            if (Normalize(key) == "page" && t.currentNode != null &&
                t.currentNode.name.StartsWith("TC_"))
            {
                string typed = "page ";
                string baseText = t.screenText.text ?? "";
                if (baseText.EndsWith(typed, StringComparison.OrdinalIgnoreCase)) return;
                string newTextPage = baseText + typed;

                var modField = t.GetType().GetField("modifyingText",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (modField != null) modField.SetValue(t, true);
                t.screenText.text = newTextPage;
                t.currentText = newTextPage;
                t.textAdded = typed.Length;

                t.screenText.ActivateInputField();
                t.screenText.stringPosition = newTextPage.Length;
                t.screenText.selectionStringFocusPosition = newTextPage.Length;
                t.screenText.ForceLabelUpdate();
                lastText = null;
                lastCharCount = -1;
                lastVisLines = -1;
                mls.LogInfo("[TerminalCursor] type 'page ' (needs arg) -> node '" + t.currentNode.name + "'");
                return;
            }



            // Команды с обязательным аргументом: вводим текст, не выполняем.
            // Игра не парсит такой глагол один (ParserError), поэтому клик только
            // подставляет "info " в конец поля — пользователь дописывает аргумент
            // и жмёт Enter сам.
            string normKey = Normalize(key);
            if (requiresArg.Contains(normKey))
            {
                string typed = key.EndsWith(" ") ? key : key + " ";
                bool customNode = t.currentNode != null && t.currentNode.name.StartsWith("TC_");
                string baseTextA = customNode ? "" : (t.currentText ?? t.screenText.text);
                // Повторный клик по той же команде не должен плодить "info info ".
                if (baseTextA.EndsWith(typed, StringComparison.OrdinalIgnoreCase)) return;
                string newTextA = baseTextA + typed;

                if (customNode)
                {
                    // TC_: текст заменяется целиком (усыхает) — TextChanged игры
                    // при textAdded < 0 вернул бы старый текст узла, поэтому
                    // гасим его modifyingText и ставим всё вручную.
                    var modField = t.GetType().GetField("modifyingText",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance);
                    if (modField != null) modField.SetValue(t, true);
                    t.screenText.text = newTextA;
                    t.currentText = newTextA;
                    t.textAdded = typed.Length;
                }
                else
                {
                    // Обычный узел: НЕ трогаем currentText/textAdded руками.
                    // Присвоение text вызывает TextChanged игры, который сам
                    // сделает textAdded += typed.Length и currentText = newTextA.
                    // Только так бэспейс стирает НАБРАННОЕ (в т.ч. несколько
                    // кликов подряд "buy info ") вплоть до текста узла: ручная
                    // подстановка currentText сворачивала ранее вставленное в
                    // "базу" узла, и стереть его было нельзя.
                    t.screenText.text = newTextA;
                    // страховка: если onValueChanged не дёрнулся (редкость),
                    // доводим состояние вручную, но только когда текст прижился.
                    if (t.screenText.text == newTextA && t.currentText != newTextA)
                    {
                        t.currentText = newTextA;
                        t.textAdded += typed.Length;
                    }
                }

                // Поле активно: в нём сразу дописывают аргумент.
                t.screenText.ActivateInputField();
                t.screenText.stringPosition = newTextA.Length;
                t.screenText.selectionStringFocusPosition = newTextA.Length;
                t.screenText.ForceLabelUpdate();
                lastText = null;
                lastCharCount = -1;
                lastVisLines = -1;
                mls.LogInfo($"[TerminalCursor] type '{typed}' (needs arg) -> node '{(t.currentNode != null ? t.currentNode.name : "null")}'");
                return;
            }

            // Штатное выполнение команды.
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
            // Считаем видимые символы — при typewriter текст появляется побуквенно.
            // isVisible меняется каждый кадр, а characterCount и maxVisibleLines — нет.
            int vis = 0;
            for (int i = 0; i < ti.characterCount; i++)
                if (ti.characterInfo[i].isVisible) vis++;
            if (txt != lastText || ti.characterCount != lastCharCount ||
                tc.maxVisibleLines != lastVisLines || vis != lastVisibleCount)
            {
                lastText = txt;
                lastCharCount = ti.characterCount;
                lastVisLines = tc.maxVisibleLines;
                lastVisibleCount = vis;
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
        // Разрешает матчинг фраз через перенос строки - только на наших страницах.
        static bool onCustomNode;

        static void UpdateClickableCache(Terminal t)
        {
            TerminalNode cn = t.currentNode;
            string nodeKey = cn != null ? cn.name : "<null>";
            // На своих страницах (TC_*) раскладку контролируем сами — разрешаем
            // фразам переноситься через строку (TMP переносит длинные строки,
            // и "ban <cmd>" рвётся, если матчинг требует одну строку).
            onCustomNode = nodeKey != null && nodeKey.StartsWith("TC_");
            if (nodeKey == cachedNodeKey) return;
            cachedNodeKey = nodeKey;
            keyMap.Clear();
            requiresArg.Clear(); // Сброс: моды могут добавлять/убирать глаголы с compatibleNouns.

            // Кастомные узлы (TC_switch, TC_ping, TC_flash, TC_history, TC_bans) — берём команды из customCmds
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
                    if (kw.compatibleNouns != null && kw.compatibleNouns.Length > 0)
                    {
                        // Глагол с совместимыми существительными — требует аргумент:
                        // сам по себе игра не парсит (ParserError). Поэтому кликаем
                        // и его тоже, но клик только ВВОДИТ "info " в поле ввода.
                        // Дедуп в AddRange + самый длинный матчинг N-грамм не дают
                        // одиночному "view" вытеснить фразу "view monitor".
                        requiresArg.Add(Normalize(kw.word));
                        AddKey(kw.word);
                        foreach (var c in kw.compatibleNouns)
                            if (c != null && c.noun != null && c.result != null && !string.IsNullOrEmpty(c.noun.word))
                                AddKey(kw.word + " " + c.noun.word);
                    }
                    continue;
                }
                // одиночное слово работает, если есть глагол по умолчанию
                // (moons/store/...), готовый узел-ответ (help, scan) или это код объекта
                if (kw.defaultVerb != null || kw.specialKeywordResult != null || kw.accessTerminalObjects)
                    AddKey(kw.word);
            }

            // TRANSMIT — нет в allKeywords, но пользователь хочет кликабельность.
            // Требует [сообщение], поэтому при клике просто вводим "TRANSMIT ".
            AddKey("TRANSMIT ");
            requiresArg.Add("transmit");

            // Добавляем ping/flash как кликабельные (их нет в allKeywords, но они есть
            // в хелпе страницы "Other commands" как команды с аргументами).
            AddKey("ping");
            AddKey("flash");
            // history — команда мода: подсвечиваем, если слово встретилось в тексте
            // (страницы модов-переводчиков/хоелп-аддонов).
            AddKey(HistoryWord);
            // del/remove — сокращения для delete (минимум 3 символа)
            AddKey("del");
            AddKey("delete");
            AddKey("rem");
            AddKey("remove");
            // ban/unban — страницы банов
            AddKey("ban");
            AddKey("unban");
            // page — навигация по папкам (требует аргумент: имя папки или $)
            AddKey("page");
            requiresArg.Add("page");
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

        // Команды, требующие аргумент: при клике не выполнять, а вводить текст
        // в терминал (например "info ", "TRANSMIT "). Нормализованные (без пробелов,
        // нижний регистр), как они хранятся в keyMap.
        static readonly HashSet<string> requiresArg = new HashSet<string>();

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
            if (entries.Length < 1)
                t.screenText.text = t.currentText[..^cmd.Length] + "radar";

            // 1 цель → выполнить напрямую (действуем сразу, возвращаем success-ноду)
            if (entries.Length < 2)
                return entries.Length > 0 ? ExecuteTargetCommandWithIndex(t, cmd, entries[0].idx) : null;

            // > 1 цели → строим список
            var sb = new StringBuilder();
            // ПУНКТ 1: берём заголовок ИЗ ИГРЫ — RTLC/XUnity уже знает эти строки
            // и переведёт их автоматически при рендере (intercepts TMP_Text.text setter).
            // specialNodes[20] = switch success node, [21] = ping, [23] = flash
            string header = null;
            var specialNodes = t.terminalNodes?.specialNodes;
            var cmdId = cmd == "switch" ? 20 : cmd == "ping" ? 21 : 23;
            if (specialNodes != null && specialNodes.Count > cmdId)
                header = specialNodes[cmdId].displayText;
            if (string.IsNullOrEmpty(header))
                header = cmd == "switch" ? cfgHeaderSwitch.Value
                      : cmd == "ping"  ? cfgHeaderPing.Value
                      : cfgHeaderFlash.Value;
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

                sb.Append(">").Append(rawLine);

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

        // ====================================================================
        //  ИСТОРИЯ КОМАНД (команда history)
        //
        //  Пишется В ОДИН файл в BepInEx\config - один на установку игры, поэтому
        //  счётчики общие для всех серверов/сохранений и живут между запусками.
        //  Ключ записи - очищенная команда в нижнем регистре, значение - сырой
        //  текст последнего ввода + счётчик. Сырой текст и отправляем в парсер
        //  игры при клике по строке истории (customCmds -> ExecuteCommand).
        // ====================================================================
        static ConfigEntry<bool> cfgHistoryEnabled;
        static ConfigEntry<int> cfgHistoryMaxEntries;
        static ConfigEntry<string> cfgHistoryWord, cfgHistoryHeader, cfgHistoryEmpty;
        static ConfigEntry<string> cfgPageNavLine, cfgFoldersBtn, cfgPageNavTemplate;
        static ConfigEntry<string> cfgHistoryDateFormat, cfgHistoryLineFormat;
        static ConfigEntry<string> cfgBannedLine, cfgBanHeader, cfgBanEmpty, cfgBanLineFormat;
        static ConfigEntry<string> cfgSortArrowAsc, cfgSortArrowDesc;
        static ConfigEntry<string> cfgSortLine, cfgSortCurrent;
        static ConfigEntry<string> cfgCreateFolderLine, cfgBackToHistoryLine;

        static readonly Dictionary<string, HistoryEntry> history =
            new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
        static bool historyLoaded;
        static string dataFile;

        // Порядок ключей = порядок их записи в JSON ("e"). Даёт фильтр
        // "sort time" (первым был использован тот, что в файле выше).
        // Пишем файл строго в этом порядке, поэтому порядок НЕ зависит от
        // внутреннего обхода Dictionary (который после удаления ключа мог бы
        // поставить новый ключ в освободившийся слот, то есть в середину).
        static readonly List<string> historyOrder = new List<string>();

        // Режим и направление сортировки страницы истории. Живёт пока открыт
        // терминал; "sort <режим>" переключает режим, повторный клик по тому же
        // режиму разворачивает направление.
        internal enum HistorySort { Count, Date, Alphabet, Time }
        static HistorySort historySort = HistorySort.Count;
        static bool historySortAsc;

        // Забаненные нормализованные команды — не попадают в историю.
        static readonly HashSet<string> bannedWords = new HashSet<string>(StringComparer.Ordinal);

        // Папки команд. Ключ = имя папки, значение = список команд.
        // "all" — всегда есть, содержит все команды с метаданными.
        static readonly Dictionary<string, List<string>> folders =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        static string currentFolder = "all"; // Текущая открытая папка
        static string armedFolder = null; // Папка для добавления команды (после "page <имя> $")

        // Защита от мусора: длина строки в истории и на экране.
        const int HistoryMaxRawLength = 120;
        const int HistoryDisplayLength = 46;
        const int HistoryLineWidth = 65;

        class HistoryEntry
        {
            public int Count;
            public long LastTicks;
        }

        static void InitHistory(BepInEx.Configuration.ConfigFile config)
        {
            cfgHistoryEnabled = config.Bind("History", "Enabled", true,
                "Record every command typed/clicked in the terminal");
            cfgHistoryMaxEntries = config.Bind("History", "MaxEntries", 200,
                "Max number of distinct commands to keep");
            cfgHistoryWord = config.Bind("Strings", "HistoryWord", "history",
                "Word that opens the command history page");
            cfgHistoryHeader = config.Bind("Strings", "HistoryHeader", "Command history:",
                "Header text of the command history page");
            cfgHistoryEmpty = config.Bind("Strings", "HistoryEmpty", "No commands recorded yet.",
                "Shown when the history is empty");
            cfgHistoryDateFormat = config.Bind("Strings", "HistoryDateFormat", "dd.MM.yyyy",
                "DateTime format of the last-used date shown in history lines");
            cfgHistoryLineFormat = config.Bind("Strings", "HistoryLineFormat",
                "№{3}>{0}  [{1}, {2}]  del {4} | ban {4}",
                "One history line; {0}=command {1}=times used {2}=last-used date {3}=padded number {4}=plain number");
            cfgBannedLine = config.Bind("Strings", "HistoryBannedLine",
                "ban [{0}]  (open banned list)",
                "Line on the history page; the word 'banned' opens the ban list ({0}=count)");
            cfgBanHeader = config.Bind("Strings", "BanListHeader", "Banned commands (won't be recorded):",
                "Header of the ban list page");
            cfgBanEmpty = config.Bind("Strings", "BanListEmpty", "Nothing is banned. Use 'ban <cmd>'.",
                "Shown when the ban list is empty");
            cfgBanLineFormat = config.Bind("Strings", "BanListLineFormat",
                "№{1}>unban {0}",
                "One ban list line; {0}=command {1}=padded number");
            cfgSortArrowAsc = config.Bind("Strings", "SortArrowAsc", "↑",
                "Arrow shown for ascending sort (A-Z / oldest first)");
            cfgSortArrowDesc = config.Bind("Strings", "SortArrowDesc", "↓",
                "Arrow shown for descending sort (Z-A / newest first)");
            cfgSortLine = config.Bind("Strings", "SortLine", "sort: {0}",
                "Clickable sort buttons line on the history page ({0}=buttons)");
            cfgSortCurrent = config.Bind("Strings", "SortCurrent", "current: {0} {1}",
                "Current sort indicator ({0}=mode {1}=arrow), not clickable");
            cfgPageNavLine = config.Bind("Strings", "PageNavLine",
                "folders: {0}",
                "Folders navigation line on the history page. {0}=folder buttons separated by ' | '");
            cfgFoldersBtn = config.Bind("Strings", "FoldersBtn",
                "page {0} [{1}]",
                "Folders navigation line on the history page ({0}=name folder {1}=number of commands per folder)");
            cfgPageNavTemplate = config.Bind("Strings", "PageNavTemplate",
                "add commands to this folder: page {0} $",
                "Template shown inside a folder. Clicking it arms the folder so the NEXT clicked command is added to it. {0}=folder name");
            cfgCreateFolderLine = config.Bind("Strings", "CreateFolderLine",
                "create folder: click page",
                "Line instructing how to create a folder");
            cfgBackToHistoryLine = config.Bind("Strings", "BackToHistoryLine",
                "{0} [{1}]  (open {0} list)",
                "Line on ban list returning to history ({0}=history word {1}=history count)");

            dataFile = Path.Combine(Paths.ConfigPath, "TerminalCursor.json");
            mls.LogInfo("[TerminalCursor] data: configPath=" + Paths.ConfigPath);
            mls.LogInfo("[TerminalCursor] data: dataFile=" + dataFile);
            LoadData();
            mls.LogInfo("[TerminalCursor] data: loaded " + history.Count + " entries, " + bannedWords.Count + " banned, " + folders.Count + " folders");
        }

        // Нормализованное слово команды истории (для перехвата и подсветки).
        internal static string HistoryWord

        {
            get { return Normalize(cfgHistoryWord != null ? cfgHistoryWord.Value : "history"); }
        }

        // Срез последнего ввода (то же, что режет парсер игры) -> в историю.
        static void RecordInput(Terminal t)
        {
            if (t.screenText == null || t.textAdded <= 0) return;
            string text = t.screenText.text;
            if (string.IsNullOrEmpty(text)) return;
            string raw = text.Substring(Mathf.Max(0, text.Length - t.textAdded));
            RecordHistory(raw);
        }

        static void RecordHistory(string rawInput)
        {
            if (cfgHistoryEnabled != null && !cfgHistoryEnabled.Value) return;
            if (rawInput == null) return;
            // Ничего не вырезаем из ввода: только Trim и нижний регистр.
            string key = rawInput.Trim().ToLowerInvariant();
            if (key.Length == 0) return;
            if (key.Length > HistoryMaxRawLength) key = key.Substring(0, HistoryMaxRawLength);
            // The 'history' command itself must not pollute the log.
            if (key == HistoryWord) return;
            // Management commands must not pollute the log.
            int sp = key.IndexOf(' ');
            string firstWord = sp < 0 ? key : key.Substring(0, sp);
            if (firstWord == "ban" || firstWord == "unban" ||
                firstWord == "delete" || firstWord == "del" ||
                firstWord == "remove" || firstWord == "rem" ||
                firstWord == "sort" || firstWord == "page") return;
            // Banned commands are skipped.
            if (bannedWords.Contains(key)) return;

            LoadData();
            HistoryEntry e;
            if (!history.TryGetValue(key, out e))
            {
                e = new HistoryEntry();
                history[key] = e;
                historyOrder.Add(key);
                // "all" — виртуальная папка (её содержимое = historyOrder),
                // отдельная запись в folders["all"] не нужна.
                TrimHistory();
            }
            e.Count++;
            e.LastTicks = DateTime.UtcNow.Ticks;
            SaveData();
        }

        // Удаление команды из истории: из словаря и из порядка.
        static bool RemoveHistoryKey(string key)
        {
            bool removed = history.Remove(key);
            historyOrder.Remove(key);
            return removed;
        }

        // Содержимое папки для показа. "all" — виртуальная: все известные команды
        // в порядке первой регистрации (historyOrder).
        static List<string> GetFolderCommands(string folder)
        {
            if (folder == "all") return new List<string>(historyOrder);
            List<string> list;
            return folders.TryGetValue(folder, out list) ? new List<string>(list) : new List<string>();
        }

        // Сортировка списка записей по текущему режиму (historySort / historySortAsc).
        static void SortEntries(List<KeyValuePair<string, HistoryEntry>> entries)
        {
            switch (historySort)
            {
                case HistorySort.Count:
                    entries.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? a.Value.Count.CompareTo(b.Value.Count)
                            : b.Value.Count.CompareTo(a.Value.Count);
                        if (c != 0) return c;
                        return string.CompareOrdinal(a.Key, b.Key);
                    });
                    break;
                case HistorySort.Date:
                    entries.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? a.Value.LastTicks.CompareTo(b.Value.LastTicks)
                            : b.Value.LastTicks.CompareTo(a.Value.LastTicks);
                        if (c != 0) return c;
                        return string.CompareOrdinal(a.Key, b.Key);
                    });
                    break;
                case HistorySort.Alphabet:
                    entries.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? string.CompareOrdinal(a.Key, b.Key)
                            : string.CompareOrdinal(b.Key, a.Key);
                        return c;
                    });
                    break;
                case HistorySort.Time:
                    {
                        var orderMap = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (int i = 0; i < historyOrder.Count; i++)
                            orderMap[historyOrder[i]] = i;
                        entries.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                        {
                            int ia, ib;
                            orderMap.TryGetValue(a.Key, out ia);
                            orderMap.TryGetValue(b.Key, out ib);
                            int c = historySortAsc ? ia - ib : ib - ia;
                            if (c != 0) return c;
                            return string.CompareOrdinal(a.Key, b.Key);
                        });
                    }
                    break;
            }
        }

        // История, отсортированная для показа: по заданному режиму и направлению.
        // "time" — порядок ключей из JSON (первой регистрации).
        static List<KeyValuePair<string, HistoryEntry>> GetSortedHistory()
        {
            var sorted = new List<KeyValuePair<string, HistoryEntry>>(history);
            switch (historySort)
            {
                case HistorySort.Count:
                    sorted.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? a.Value.Count.CompareTo(b.Value.Count)
                            : b.Value.Count.CompareTo(a.Value.Count);
                        if (c != 0) return c;
                        return string.CompareOrdinal(a.Key, b.Key);
                    });
                    break;
                case HistorySort.Date:
                    sorted.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? a.Value.LastTicks.CompareTo(b.Value.LastTicks)
                            : b.Value.LastTicks.CompareTo(a.Value.LastTicks);
                        if (c != 0) return c;
                        return string.CompareOrdinal(a.Key, b.Key);
                    });
                    break;
                case HistorySort.Alphabet:
                    sorted.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                    {
                        int c = historySortAsc
                            ? string.CompareOrdinal(a.Key, b.Key)
                            : string.CompareOrdinal(b.Key, a.Key);
                        return c;
                    });
                    break;
                case HistorySort.Time:
                    {
                        // Индекс в historyOrder — порядок первой регистрации.
                        // Собираем карту для быстрого доступа.
                        var orderMap = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (int i = 0; i < historyOrder.Count; i++)
                            orderMap[historyOrder[i]] = i;
                        sorted.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                        {
                            int ia, ib;
                            orderMap.TryGetValue(a.Key, out ia);
                            orderMap.TryGetValue(b.Key, out ib);
                            int c = historySortAsc ? ia - ib : ib - ia;
                            if (c != 0) return c;
                            return string.CompareOrdinal(a.Key, b.Key);
                        });
                    }
                    break;
            }
            return sorted;
        }

        // Аргумент del/ban/unban: либо номер строки истории ("2"), либо сама
        // команда ("buy shovel"). Номер резолвится по той же сортировке, что и
        // страница, поэтому "del 2" == клик по "del" во второй строке.
        static string ResolveHistoryArg(string rawArg)
        {
            if (string.IsNullOrEmpty(rawArg)) return rawArg;
            int num;
            if (!int.TryParse(rawArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                return rawArg;
            if (num < 1) return rawArg;

            // Собираем пары из текущей папки и сортируем по активному режиму —
            // тот же порядок, что показывает страница, поэтому "del 2" == клик
            // по "del" во второй строке.
            var sorted = new List<KeyValuePair<string, HistoryEntry>>();
            foreach (string cmd in GetFolderCommands(currentFolder))
            {
                HistoryEntry e;
                if (history.TryGetValue(cmd, out e))
                {
                    sorted.Add(new KeyValuePair<string, HistoryEntry>(cmd, e));
                }
            }
            SortEntries(sorted);
            
            if (num > sorted.Count) return rawArg;
            return sorted[num - 1].Key;
        }

        // Динамический узел TC_history: строки
        // "№N > <команда>  [N, дата]  del N | ban N".
        // Клик по команде выполняет её; клик по "del N" / "ban N" удаляет /
        // банит её и сразу перерисовывает страницу (хендлеры возвращают узел).
        internal static TerminalNode BuildHistoryNode(Terminal t)
        {
            LoadData();

            // Собираем пары из текущей папки ("all" = вся история) и сортируем
            // по активному режиму.
            var sorted = new List<KeyValuePair<string, HistoryEntry>>();
            foreach (string cmd in GetFolderCommands(currentFolder))
            {
                HistoryEntry e;
                if (history.TryGetValue(cmd, out e))
                {
                    sorted.Add(new KeyValuePair<string, HistoryEntry>(cmd, e));
                }
            }
            // Применяем текущий режим сортировки.
            SortEntries(sorted);

            customCmds.Clear();
            customCmds.Add("page");  // всегда кликабельно для строки "create folder"

            var sb = new StringBuilder();
            sb.AppendLine(cfgHistoryHeader.Value);
            sb.AppendLine();

            if (bannedWords.Count > 0)
            {
                sb.AppendLine(SafeFormat(cfgBannedLine, bannedWords.Count));
                sb.AppendLine();
            }

            // Строка фильтров сортировки — кликабельна.
            AddSortButtons(sb);
            sb.AppendLine();

            // Навигация между папками.
            AddFolderNavigation(sb);
            sb.AppendLine();

            // Подсказка о создании папки.
            sb.AppendLine(cfgCreateFolderLine.Value);
            sb.AppendLine();

            // Уведомление об арме (добавление в папку).
            if (!string.IsNullOrEmpty(armedFolder))
            {
                sb.AppendLine("click a command to add it to folder '" + armedFolder + "'");
                sb.AppendLine();
            }

            if (sorted.Count == 0)
            {
                sb.AppendLine(cfgHistoryEmpty.Value);
            }
            else
            {
                int shown = sorted.Count; // Все команды на одной странице
                // Длина номера для выравнивания: "№10 " vs "№1  "
                int numWidth = (shown.ToString(CultureInfo.InvariantCulture) + " ").Length;
                for (int i = 0; i < shown; i++)
                {
                    string cmd = sorted[i].Key;
                    string num = (i + 1).ToString(CultureInfo.InvariantCulture);
                    // Для отображения — с символом и пробелами.
                    string numPad = num.PadRight(numWidth);
                    // Для команд — только число.
                    string numCmd = num;
                    customCmds.Add(cmd);
                    customCmds.Add("del " + numCmd);
                    customCmds.Add("ban " + numCmd);
                    string date = FormatTicks(sorted[i].Value.LastTicks);
                    string cnt = sorted[i].Value.Count.ToString(CultureInfo.InvariantCulture);
                    // Одна строка: если TMP перенесёт её на вторую — не страшно,
                    // на TC_ страницах матчинг идёт по подстроке всего текста.
                    // {3} — номер с № и выравниванием (левый столбец),
                    // {4} — чистый номер: он же лежит в customCmds, поэтому
                    // кнопки "del 1"/"ban 1" матчатся и кликабельны.
                    sb.AppendLine(SafeFormat(cfgHistoryLineFormat, cmd, cnt, date, numPad, numCmd));
                }
            }

            // Слово 'ban' из строки "banned [N] (open ban list)" должно быть
            // кликабельным -> страница банов. keyMap на TC_ нодах строится только
            // из customCmds, поэтому добавляем его сюда.
            if (bannedWords.Count > 0)
            {
                customCmds.Add("ban");
            }

            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.name = "TC_history";
            node.displayText = sb.ToString();
            node.clearPreviousText = true;

            if (lastCustomNode != null) UnityEngine.Object.Destroy(lastCustomNode);
            lastCustomNode = node;
            cachedNodeKey = null;
            return node;
        }

        // Динамический узел TC_bans: список забаненных команд.
        // Клик по "unban <команда>" разбанивает и перерисовывает список.
        internal static TerminalNode BuildBanListNode(Terminal t)
        {
            LoadData();
            customCmds.Clear();

            var sb = new StringBuilder();
            sb.AppendLine(cfgBanHeader.Value);
            sb.AppendLine();

            // Возврат к истории — вверху списка, кликабельно.
            customCmds.Add(HistoryWord);
            sb.AppendLine(SafeFormat(cfgBackToHistoryLine, cfgHistoryWord.Value, history.Count));
            sb.AppendLine();

            if (bannedWords.Count == 0)
            {
                sb.AppendLine(cfgBanEmpty.Value);
            }
            else
            {
                var list = new List<string>(bannedWords);
                list.Sort(StringComparer.Ordinal);
                int shown = list.Count; // Все баны на одной странице
                // Длина номера для выравнивания
                int numWidth = (shown.ToString(CultureInfo.InvariantCulture) + " ").Length;
                for (int i = 0; i < shown; i++)
                {
                    string w = list[i];
                    string num = (i + 1).ToString(CultureInfo.InvariantCulture);
                    string numPad = num.PadRight(numWidth);
                    string disp = w.Length > HistoryDisplayLength - 14
                        ? w.Substring(0, HistoryDisplayLength - 17) + "..."
                        : w;
                    customCmds.Add("unban " + w);
                    sb.AppendLine(SafeFormat(cfgBanLineFormat, disp, numPad));
                }
            }

            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.name = "TC_bans";
            node.displayText = sb.ToString();
            node.clearPreviousText = true;

            if (lastCustomNode != null) UnityEngine.Object.Destroy(lastCustomNode);
            lastCustomNode = node;
            cachedNodeKey = null;
            return node;
        }

        // Кнопки сортировки: строка кнопок + строка текущего режима.
        // Кнопка показывает направление, которое БУДЕТ применено при клике
        // (для активного режима — противоположное текущему).
        static void AddSortButtons(StringBuilder sb)
        {
            string asc = cfgSortArrowAsc != null ? cfgSortArrowAsc.Value : "↑";
            string desc = cfgSortArrowDesc != null ? cfgSortArrowDesc.Value : "↓";

            var btns = new List<string>(4);
            AppendSortButton(btns, HistorySort.Alphabet, "alphabet", asc, desc);
            AppendSortButton(btns, HistorySort.Count, "count", asc, desc);
            AppendSortButton(btns, HistorySort.Date, "date", asc, desc);
            AppendSortButton(btns, HistorySort.Time, "time", asc, desc);

            sb.AppendLine(SafeFormat(cfgSortLine, string.Join(" | ", btns)));

            // Текущий режим — БЕЗ слова "sort", чтобы матчинг по подстроке не
            // сделал эту строку кликабельной (по условию она не кликабельна).
            string curName = SortName(historySort);
            string curArrow = historySortAsc ? asc : desc;
            sb.AppendLine(SafeFormat(cfgSortCurrent, curName, curArrow));
        }

        static void AppendSortButton(List<string> btns, HistorySort mode, string name,
            string asc, string desc)
        {
            // Клик по активному режиму разворачивает текущий; по неактивному —
            // включает режим с его направлением по умолчанию.
            bool willAsc = mode == historySort ? !historySortAsc : SortDefaultAsc(mode);
            btns.Add("sort " + name + " " + (willAsc ? asc : desc));
            customCmds.Add("sort " + name);
        }

        // Строка навигации между папками: "page store | page tools"
        // + строка-шаблон "page <current> $" (кроме all).
        static void AddFolderNavigation(StringBuilder sb)
        {
            // Все папки, кроме текущей.
            var btns = new List<string>();
            List<string> foldersArr = ["all", ..folders.Keys];
            foreach (var kv in foldersArr)
            {
                if (kv == currentFolder) continue;
                btns.Add(kv);
            }
            if (btns.Count > 0)
            {
                var navBtns = new List<string>();
                foreach (string f in btns)
                {
                    var countFolder = f == "all" ? history.Count : folders[f].Count;
                    var foldersBtnText = cfgFoldersBtn.Value;
                    if (countFolder < 1)
                        cfgFoldersBtn.Value = foldersBtnText[..foldersBtnText.LastIndexOf(' ')];
                    navBtns.Add(SafeFormat(cfgFoldersBtn, countFolder < 1 ? [f] : [f, countFolder]));
                    cfgFoldersBtn.Value = foldersBtnText;
                    customCmds.Add("page " + f);
                }
                string joined = string.Join(" | ", navBtns);
                sb.AppendLine(SafeFormat(cfgPageNavLine, joined));
            }

            // Шаблон: "page <current> $" — кликабельный (кроме all).
            if (currentFolder != "all")
            {
                sb.AppendLine(SafeFormat(cfgPageNavTemplate, currentFolder));
                customCmds.Add($"page {currentFolder} $");
            }
        }

        static string SortName(HistorySort m)
        {
            switch (m)
            {
                case HistorySort.Alphabet: return "alphabet";
                case HistorySort.Date: return "date";
                case HistorySort.Time: return "time";
                default: return "count";
            }
        }

        // Направление по умолчанию: alphabet/time — ↑ (A-Z, сначала старые),
        // count/date — ↓ (чаще/свежего выше).
        static bool SortDefaultAsc(HistorySort m)
        {
            return m == HistorySort.Alphabet || m == HistorySort.Time;
        }

        internal static TerminalNode HandleSort(Terminal t, string rawArg)
        {
            string a = rawArg == null ? "" : rawArg.Trim().ToLowerInvariant();
            HistorySort mode;
            if (a == "alphabet" || a == "alpha" || a == "name") mode = HistorySort.Alphabet;
            else if (a == "count" || a == "c" || a == "uses") mode = HistorySort.Count;
            else if (a == "date" || a == "last") mode = HistorySort.Date;
            else if (a == "time" || a == "first" || a == "json" || a == "order") mode = HistorySort.Time;
            else return BuildHistoryNode(t);

            if (mode == historySort) historySortAsc = !historySortAsc;
            else { historySort = mode; historySortAsc = SortDefaultAsc(mode); }

            mls.LogInfo("[TerminalCursor] sort -> " + SortName(mode) + (historySortAsc ? " asc" : " desc"));
            return BuildHistoryNode(t);
        }

        // page <имя> — открыть/создать папку.
        // page <имя> $ — армить добавление команды в папку (возвращает в all).
        // page all — вернуться к полной истории.
        internal static TerminalNode HandlePage(Terminal t, string rawArg)
        {
            LoadData();
            mls.LogInfo("[TerminalCursor] HandlePage rawArg='" + rawArg + "'");
            string a = rawArg == null ? "" : rawArg.Trim().ToLowerInvariant();
            if (a.Length == 0) return BuildHistoryNode(t);

            // Спецсимвол '$' в конце — арм добавления.
            bool arm = a.EndsWith("$");
            if (arm) a = a.Substring(0, a.Length - 1).Trim();
            if (a.Length == 0) return BuildHistoryNode(t);

            // "all" — виртуальная папка: открывается, но не создаётся и не армится.
            if (a == "all")
            {
                currentFolder = "all";
                armedFolder = null;
                mls.LogInfo("[TerminalCursor] opened folder: 'all'");
                return BuildHistoryNode(t);
            }

            if (arm)
            {
                if (!folders.ContainsKey(a))
                {
                    folders[a] = new List<string>();
                    SaveData();
                }
                currentFolder = "all";
                armedFolder = a;
                mls.LogInfo("[TerminalCursor] armed: next command goes to '" + a + "'");
                return BuildHistoryNode(t);
            }

            if (!folders.ContainsKey(a))
            {
                folders[a] = new List<string>();
                SaveData();
            }
            currentFolder = a;
            armedFolder = null;
            mls.LogInfo("[TerminalCursor] opened folder: '" + currentFolder + "'");
            return BuildHistoryNode(t);
        }

        // string.Format с падением на безопасный вариант (конфиг правят руками).
        static string SafeFormat(ConfigEntry<string> fmt, params object[] args)
        {
            try { return string.Format(fmt.Value, args); }
            catch (FormatException) { return string.Format((string)fmt.DefaultValue, args); }
            catch (IndexOutOfRangeException) { return string.Format((string)fmt.DefaultValue, args); }
        }

        static string FormatTicks(long ticks)
        {
            string fmt = cfgHistoryDateFormat != null ? cfgHistoryDateFormat.Value : "dd.MM";
            try { return new DateTime(ticks, DateTimeKind.Utc).ToString(fmt); }
            catch (FormatException) { return ""; }
        }

        // Слово уже есть у игры (ванилла или другой мод)? Не перехватываем.
        internal static bool VanillaKeywordExists(Terminal t, string normWord)

        {
            TerminalNodesList list = t != null ? t.terminalNodes : null;
            if (list == null || list.allKeywords == null) return false;
            foreach (TerminalKeyword kw in list.allKeywords)
                if (kw != null && Normalize(kw.word) == normWord) return true;
            return false;
        }

        // Сверх лимита выкидываем самые редкие (при равных - самые давние).
        static void TrimHistory()
        {
            int max = cfgHistoryMaxEntries != null ? Math.Max(10, cfgHistoryMaxEntries.Value) : 200;
            if (history.Count <= max) return;

            var all = new List<KeyValuePair<string, HistoryEntry>>(history);
            all.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
            {
                int c = a.Value.Count.CompareTo(b.Value.Count);
                return c != 0 ? c : a.Value.LastTicks.CompareTo(b.Value.LastTicks);
            });
            int drop = history.Count - max;
            for (int i = 0; i < drop && i < all.Count; i++)
            {
                string dk = all[i].Key;
                history.Remove(dk);
                historyOrder.Remove(dk);
            }
        }

        static void LoadData()
        {
            if (historyLoaded) return;
            historyLoaded = true;
            if (string.IsNullOrEmpty(dataFile) || !File.Exists(dataFile)) return;
            try
            {
                string json = File.ReadAllText(dataFile, Encoding.UTF8);
                
                // Загружаем "all" — полноценный словарь с метаданными.
                int allStart = json.IndexOf("\"all\"");
                if (allStart >= 0)
                {
                    int braceStart = json.IndexOf('{', allStart);
                    if (braceStart >= 0)
                    {
                        int braceEnd = FindMatchingBracket(json, braceStart);
                        if (braceEnd >= 0)
                        {
                            string obj = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
                            int pos = 0;
                            while (pos < obj.Length)
                            {
                                int keyStart = obj.IndexOf('"', pos);
                                if (keyStart < 0) break;
                                int keyEnd = obj.IndexOf('"', keyStart + 1);
                                if (keyEnd < 0) break;
                                string key = obj.Substring(keyStart + 1, keyEnd - keyStart - 1);
                                pos = keyEnd + 1;
                                int colon = obj.IndexOf(':', pos);
                                if (colon < 0) break;
                                int valStart = obj.IndexOf('{', colon);
                                if (valStart < 0) break;
                                int valEnd = obj.IndexOf('}', valStart);
                                if (valEnd < 0) break;
                                pos = valEnd + 1;
                                string valObj = obj.Substring(valStart + 1, valEnd - valStart - 1);
                                int count = JsonReadInt(valObj, "c", 1);
                                long ticks = JsonReadLong(valObj, "t", 0);
                                if (key.Length == 0) continue;
                                history[key] = new HistoryEntry
                                {
                                    Count = Math.Max(1, count),
                                    LastTicks = ticks
                                };
                                if (!historyOrder.Contains(key)) historyOrder.Add(key);
                            }
                        }
                    }
                }
                
                // Загружаем "e" — словарь пользовательских папок.
                // "all" — виртуальная (из historyOrder), не храним и не грузим из "e".
                int eStart = json.IndexOf("\"e\"");
                if (eStart >= 0)
                {
                    int objStart = json.IndexOf('{', eStart);
                    if (objStart >= 0)
                    {
                        int objEnd = FindMatchingBracket(json, objStart);
                        if (objEnd >= 0)
                        {
                            string obj = json.Substring(objStart + 1, objEnd - objStart - 1);
                            int pos = 0;
                            while (pos < obj.Length)
                            {
                                int keyStart = obj.IndexOf('"', pos);
                                if (keyStart < 0) break;
                                int keyEnd = obj.IndexOf('"', keyStart + 1);
                                if (keyEnd < 0) break;
                                string folderName = obj.Substring(keyStart + 1, keyEnd - keyStart - 1);
                                pos = keyEnd + 1;
                                int colon = obj.IndexOf(':', pos);
                                if (colon < 0) break;
                                int arrStart = obj.IndexOf('[', colon);
                                if (arrStart < 0) break;
                                int arrEnd = obj.IndexOf(']', arrStart);
                                if (arrEnd < 0) break;
                                pos = arrEnd + 1;
                                string items = obj.Substring(arrStart + 1, arrEnd - arrStart - 1);
                                
                                var cmdList = new List<string>();
                                int ipos = 0;
                                while (ipos < items.Length)
                                {
                                    int s = items.IndexOf('"', ipos);
                                    if (s < 0) break;
                                    int e = items.IndexOf('"', s + 1);
                                    if (e < 0) break;
                                    cmdList.Add(items.Substring(s + 1, e - s - 1));
                                    ipos = e + 1;
                                }
                                // "all" — виртуальная, пропускаем (дубликат historyOrder).
                                // Пустые папки тоже сохраняем.
                                if (folderName != "all")
                                {
                                    folders[folderName] = cmdList;
                                }
                            }
                        }
                    }
                }
                
                int bStart = json.IndexOf("\"b\"");
                if (bStart >= 0)
                {
                    bStart = json.IndexOf('[', bStart);
                    if (bStart >= 0)
                    {
                        int bEnd = FindMatchingBracket(json, bStart);
                        if (bEnd >= 0)
                        {
                            string items = json.Substring(bStart + 1, bEnd - bStart - 1);
                            int pos = 0;
                            while (pos < items.Length)
                            {
                                int s = items.IndexOf('"', pos);
                                if (s < 0) break;
                                int e = items.IndexOf('"', s + 1);
                                if (e < 0) break;
                                pos = e + 1;
                                bannedWords.Add(items.Substring(s + 1, e - s - 1).ToLowerInvariant());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                mls.LogWarning("[TerminalCursor] data: failed to read file: " + ex.Message);
            }
        }

        static void SaveData()
        {
            if (string.IsNullOrEmpty(dataFile)) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"v\":2,\"all\":{");
                bool first = true;
                // Пишем "all" — полноценный словарь с метаданными.
                foreach (string k in historyOrder)
                {
                    HistoryEntry ent;
                    if (!history.TryGetValue(k, out ent)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonQuote(k))
                      .Append(":{\"c\":")
                      .Append(ent.Count)
                      .Append(",\"t\":").Append(ent.LastTicks)
                      .Append('}');
                }
                // Страховка: ключ есть в словаре, но потерялся в порядке.
                var written = new HashSet<string>(historyOrder, StringComparer.Ordinal);
                foreach (KeyValuePair<string, HistoryEntry> kv in history)
                {
                    if (written.Contains(kv.Key)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonQuote(kv.Key))
                      .Append(":{\"c\":")
                      .Append(kv.Value.Count)
                      .Append(",\"t\":").Append(kv.Value.LastTicks)
                      .Append('}');
                }
                sb.Append("},\"e\":{");
                first = true;
                // Пишем только пользовательские папки: "all" — виртуальная
                // (содержимое = historyOrder), дублировать её в "e" не нужно.
                foreach (var kv in folders)
                {
                    if (kv.Key == "all") continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonQuote(kv.Key)).Append(":[");
                    bool firstCmd = true;
                    foreach (string cmd in kv.Value)
                    {
                        if (!firstCmd) sb.Append(',');
                        firstCmd = false;
                        sb.Append(JsonQuote(cmd));
                    }
                    sb.Append(']');
                }
                sb.Append("},\"b\":[");
                first = true;
                foreach (string w in bannedWords)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonQuote(w));
                }
                sb.Append("]}");
                File.WriteAllText(dataFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                mls.LogWarning("[TerminalCursor] data: failed to write file: " + ex.Message);
            }
        }

        // Найти закрывающую ] или } по открывающей [ или { (учёт вложенности и строк).
        // Глубину уменьшает ЛЮБАЯ закрывающая скобка: внутри объекта с массивами
        // встреченный ']' обязан уменьшить счётчик, иначе глубина никогда не
        // вернётся к нулю и папки ("e" содержит массивы) не загрузятся.
        static int FindMatchingBracket(string json, int open)
        {
            int depth = 0, len = json.Length;
            bool inStr = false;
            for (int i = open; i < len; i++)
            {
                char c = json[i];
                if (inStr && c == (char)92) { i++; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}')
                {
                    depth--;
                    if (depth <= 0) return i;
                }
            }
            return -1;
        }

        static int JsonReadInt(string obj, string key, int def)
        {
            int k = obj.IndexOf('"' + key + '"');
            if (k < 0) return def;
            int s = obj.IndexOf(':', k + key.Length + 1);
            if (s < 0) return def;
            int e = s + 1;
            while (e < obj.Length && (char.IsDigit(obj[e]) || obj[e] == '-')) e++;
            int val;
            int.TryParse(obj.Substring(s + 1, e - s - 1), out val);
            return val;
        }

        static long JsonReadLong(string obj, string key, long def)
        {
            int k = obj.IndexOf('"' + key + '"');
            if (k < 0) return def;
            int s = obj.IndexOf(':', k + key.Length + 1);
            if (s < 0) return def;
            int e = s + 1;
            while (e < obj.Length && (char.IsDigit(obj[e]) || obj[e] == '-')) e++;
            long val;
            long.TryParse(obj.Substring(s + 1, e - s - 1), out val);
            return val;
        }

        static string JsonQuote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append('\\').Append('"'); break;
                    case '\\': sb.Append('\\').Append('\\'); break;
                    case '\n': sb.Append('\\').Append('n'); break;
                    case '\r': sb.Append('\\').Append('r'); break;
                    case '\t': sb.Append('\\').Append('t'); break;
                    default:   sb.Append(c); break;

                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ==================================================================
        //  Команды: delete/del/remove/rem <cmd> — удалить из истории,
        //  ban <cmd> — запретить (удаляет из истории сразу, перестанет
        //  записываться в дальнейшем).
        //  Возвращают свежую страницу (history/список банов) — терминал
        //  сразу показывает обновлённый список.
        //  Аргумент может быть номером строки ("2") или самой командой.
        // ==================================================================
        internal static TerminalNode HandleDelete(Terminal t, string rawArg)
        {
            LoadData();
            string key = rawArg.Trim().ToLowerInvariant();
            if (key.Length > HistoryMaxRawLength) key = key.Substring(0, HistoryMaxRawLength);
            if (key.Length > 0) key = ResolveHistoryArg(key);
            if (key.Length > 0)
            {
                if (currentFolder == "all")
                {
                    // Удаляем из всех папок и из all.
                    foreach (var folder in folders.Values)
                    {
                        folder.Remove(key);
                    }
                    RemoveHistoryKey(key);
                }
                else
                {
                    // Удаляем только из текущей папки.
                    if (folders.ContainsKey(currentFolder))
                    {
                        folders[currentFolder].Remove(key);
                        SaveData();
                    }
                }
                SaveData();
                mls.LogInfo("[TerminalCursor] delete '" + rawArg + "' -> removed");
            }
            else
            {
                mls.LogInfo("[TerminalCursor] delete '" + rawArg + "' -> not found");
            }
            return BuildHistoryNode(t);
        }

        // ban удаляет команду из истории и запрещает ей появляться в дальнейшем.
        internal static TerminalNode HandleBan(Terminal t, string rawArg)
        {
            LoadData();
            string key = rawArg.Trim().ToLowerInvariant();
            if (key.Length > HistoryMaxRawLength) key = key.Substring(0, HistoryMaxRawLength);
            if (key.Length > 0) key = ResolveHistoryArg(key);
            if (key.Length > 0)
            {
                // Удаляем из всех папок.
                foreach (var folder in folders.Values)
                {
                    folder.Remove(key);
                }
                history.Remove(key);
                historyOrder.Remove(key);
                if (bannedWords.Add(key))
                {
                    SaveData();
                    mls.LogInfo("[TerminalCursor] ban '" + rawArg + "' -> banned");
                }
                else
                {
                    mls.LogInfo("[TerminalCursor] ban '" + rawArg + "' -> already banned");
                }
            }
            return BuildHistoryNode(t);
        }

        internal static TerminalNode HandleUnban(Terminal t, string rawArg)
        {
            LoadData();
            string key = rawArg.Trim().ToLowerInvariant();
            if (key.Length > HistoryMaxRawLength) key = key.Substring(0, HistoryMaxRawLength);
            if (key.Length > 0) key = ResolveHistoryArg(key);
            if (key.Length > 0 && bannedWords.Remove(key))
            {
                SaveData();
                mls.LogInfo("[TerminalCursor] unban '" + rawArg + "' -> unbanned");
            }
            else
            {
                mls.LogInfo("[TerminalCursor] unban '" + rawArg + "' -> not banned");
            }
            return BuildBanListNode(t);
        }

        // ==================================================================


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
    //  - "ping" без аргумента → specialNodes[10] → "[There was no actionhistoryhistory
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

            // Сырой срез ДО RemovePunctuation: игра снимает пунктуацию, а спецсимвол
            // '$' (арм добавления в папку) нужен нам целым.
            string raw = s;

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
                // delete/del/remove/rem <cmd> / ban <cmd> / unban <cmd> /
                // sort <mode> — управление историей.
                string lower0 = parts[0].ToLowerInvariant();
                if (lower0 == "delete" || lower0 == "del" || lower0 == "remove" || lower0 == "rem")
                { __result = TerminalCursorPlugin.HandleDelete(__instance, s.Substring(parts[0].Length).Trim()); return false; }
                if (lower0 == "ban")
                { __result = TerminalCursorPlugin.HandleBan(__instance, s.Substring(parts[0].Length).Trim()); return false; }
                if (lower0 == "unban")
                { __result = TerminalCursorPlugin.HandleUnban(__instance, s.Substring(parts[0].Length).Trim()); return false; }
                if (lower0 == "sort")
                { __result = TerminalCursorPlugin.HandleSort(__instance, s.Substring(parts[0].Length).Trim()); return false; }
                // page <folder> [ $ ] — навигация / арм. Сырой срез (raw) сохраняет '$'.
                if (lower0 == "page")
                { __result = TerminalCursorPlugin.HandlePage(__instance, raw.Substring(parts[0].Length).Trim()); return false; }

                return true;
            }

            string cmd = parts[0].ToLowerInvariant();
            // history: команда наша (у игры её нет) - показываем страницу истории.
            if (cmd == TerminalCursorPlugin.HistoryWord &&
                !TerminalCursorPlugin.VanillaKeywordExists(__instance, cmd))
            {
                __result = TerminalCursorPlugin.BuildHistoryNode(__instance);
                return false;
            }
            // ban/unban без аргумента -> страница со списком забаненных команд.
            if ((cmd == "ban" || cmd == "unban") &&
                !TerminalCursorPlugin.VanillaKeywordExists(__instance, cmd))
            {
                __result = TerminalCursorPlugin.BuildBanListNode(__instance);
                return false;
            }
            // delete/del/remove/rem без аргумента -> обновлённая страница истории.
            if ((cmd == "delete" || cmd == "del" || cmd == "remove" || cmd == "rem") &&
                !TerminalCursorPlugin.VanillaKeywordExists(__instance, cmd))
            {
                __result = TerminalCursorPlugin.BuildHistoryNode(__instance);
                return false;
            }
            // sort без аргумента -> обновлённая страница с тем же режимом сортировки.
            if (cmd == "sort")
            {
                __result = TerminalCursorPlugin.HandleSort(__instance, "");
                return false;
            }
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