using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.IO;

using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TerminalCursor
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class TerminalCursorPlugin : BaseUnityPlugin
    {
        internal static readonly Dictionary<string, (string method, int idx)> PlayerCmds = new(){ ["switch"] = ("SwitchRadarTargetAndSync", 20), ["ping"] = ("PingRadarBooster", 21), ["flash"] = ("FlashRadarBooster", 23), };
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
        static readonly List<int> cmdStarts = []; // границы всех командных слов
        static readonly List<int> cmdEnds = [];
        static readonly List<string> cmdKeys = []; // сырой ключ для ExecuteCommand

        // Нормализованные → сырые ключи
        static readonly Dictionary<string, string> keyMap = [];

        // Нормализованные display-имена товаров из текста (текст между "* " и "//").
        // Строится один раз на узел, используется для N-граммного матчинга.
        static readonly Dictionary<string, string> displayKeyMap = [];

        // Кэш узла: пересборка при смене терминального узла
        static string cachedNodeKey;

        // Динамические команды для страниц switch/ping/flash (target list)
        static readonly List<string> customCmds = [];
        static TerminalNode lastCustomNode;

        // Свой runner-объект: GameObject самого плагина игра уничтожает при загрузке
        // первой сцены, поэтому Update плагина не вызывается. Объект, созданный уже
        // при загруженной сцене (DontDestroyOnLoad + HideAndDontSave), живёт.
        static TerminalCursorRunner runner;

        private void Awake()
        {
            // Инициализация config-строк (для переводчиков: XUnity и др.)
            cfgHistoryEnabled = Config.Bind("History", "HistoryEnabled", true, "Record every command typed/clicked in the terminal");
            cfgMax = Config.Bind("History", "HistoryMaxEntries", 200, new ConfigDescription("Max number of distinct commands to keep", new AcceptableValueRange<int>(10, int.MaxValue)));
            cfgHistoryMaxRawLength = Config.Bind("History", "MaxRawLength", 120, new ConfigDescription("Max recorded command length in chars", new AcceptableValueRange<int>(10, 500)));
            cfgHeaderSwitch = Config.Bind("Strings", "HeaderSwitch", "Switching radar cam view.", "Header text for switch target list page");
            cfgHeaderPing = Config.Bind("Strings", "HeaderPing", "Ping radar booster.", "Header text for ping target list page");
            cfgHeaderFlash = Config.Bind("Strings", "HeaderFlash", "Flash radar booster.", "Header text for flash target list page");
            cfgMarkCurrent = Config.Bind("Strings", "MarkCurrent", "(current)", "Label for the current radar target");
            cfgMarkRadar = Config.Bind("Strings", "MarkRadar", "(radar)", "Label for non-player radar targets");
            cfgHistoryWord = Config.Bind("Strings", "HistoryWord", "history", "Word that opens the command history page");
            cfgHistoryHeader = Config.Bind("Strings", "HistoryHeader", "Command history:", "Header text of the command history page");
            cfgHistoryEmpty = Config.Bind("Strings", "HistoryEmpty", "No commands recorded yet.", "Shown when the history is empty");
            cfgHistoryDateFormat = Config.Bind("Strings", "HistoryDateFormat", "dd.MM.yyyy", "DateTime format of the last-used date shown in history lines");
            cfgHistoryLineFormat = Config.Bind("Strings", "HistoryLineFormat", "№{3}>{0}  [{1}, {2}]  del {4} | ban {4}", "One history line; {0}=command {1}=times used {2}=last-used date {3}=padded number {4}=plain number");
            cfgBannedLine = Config.Bind("Strings", "HistoryBannedLine", "ban [{0}]  (open banned list)", "Line on the history page; the word 'banned' opens the ban list ({0}=count)");
            cfgBanHeader = Config.Bind("Strings", "BanListHeader", "Banned commands (won't be recorded):", "Header of the ban list page");
            cfgBanEmpty = Config.Bind("Strings", "BanListEmpty", "Nothing is banned. Use 'ban <cmd>'.", "Shown when the ban list is empty");
            cfgBanLineFormat = Config.Bind("Strings", "BanListLineFormat", "№{1}>unban {0}", "One ban list line; {0}=command {1}=padded number");
            cfgSortArrowAsc = Config.Bind("Strings", "SortArrowAsc", "↑", "Arrow shown for ascending sort (A-Z / oldest first)");
            cfgSortArrowDesc = Config.Bind("Strings", "SortArrowDesc", "↓", "Arrow shown for descending sort (Z-A / newest first)");
            cfgSortLine = Config.Bind("Strings", "SortLine", "sort: {0}", "Clickable sort buttons line on the history page ({0}=buttons)");
            cfgSortCurrent = Config.Bind("Strings", "SortCurrent", "current: {0} {1}", "Current sort indicator ({0}=mode {1}=arrow), not clickable");
            cfgPageNavLine = Config.Bind("Strings", "PageNavLine", "folders: {0}", "Folders navigation line on the history page. {0}=folder buttons separated by ' | '");
            cfgFoldersBtn = Config.Bind("Strings", "FoldersBtn", "page {0} [{1}]", "Folders navigation line on the history page ({0}=name folder {1}=number of commands per folder)");
            cfgPageNavTemplate = Config.Bind("Strings", "PageNavTemplate", "add commands to this folder: page {0} $", "Template shown inside a folder. Clicking it arms the folder so the NEXT clicked command is added to it. {0}=folder name");
            cfgCreateFolderLine = Config.Bind("Strings", "CreateFolderLine", "create folder: click page", "Line instructing how to create a folder");
            cfgBackToHistoryLine = Config.Bind("Strings", "BackToHistoryLine", "{0} [{1}]  (open {0} list)", "Line on ban list returning to history ({0}=history word {1}=history count)");
            // Plugin startup logic
            mls = Logger;
            mls.LogInfo("data: configPath=" + Paths.ConfigPath);
            dataFile = Path.Combine(Paths.ConfigPath, "TerminalCursor.json");
            mls.LogInfo("data: dataFile=" + dataFile);
            LoadData();
            mls.LogInfo($"data: loaded {history.Count} entries, {bannedWords.Count} banned, {folders.Count} folders");
            On.Terminal.BeginUsingTerminal += Terminal_BeginUsingTerminal;
            On.Terminal.QuitTerminal += Terminal_QuitTerminal;
            On.Terminal.OnSubmit += Terminal_OnSubmit;
            On.GameNetcodeStuff.PlayerControllerB.KillPlayer += PlayerControllerB_KillPlayer;
            mls.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            // Патч OnFillVBO - единственная точка, где можно выставить каретку по
            // актуальному textInfo (см. комментарий у Patch_OnFillVBO).
            CaretAccess.Init();
            try { new Harmony(PluginInfo.PLUGIN_GUID).PatchAll(typeof(TerminalCursorPlugin).Assembly); }
            catch (Exception ex) { mls.LogError("failed to patch TMP_InputField: " + ex); }
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
            if (self?.screenText is {} f) f.onFocusSelectAll = false;
            orig(self);
            mls.LogWarning("--- Terminal usage started explicitly by system hook. ---");
            locked = Cursor.lockState;
            wasVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            StartOfRound.Instance.localPlayerController?.disableLookInput = true;
            currentTerminal = self;
            currentFolder = "all";
            armedFolder = "";
            ResetLayoutCache();
            cachedNodeKey = null;
            keyMap.Clear(); displayKeyMap.Clear(); ResetCmdRanges();
            // При повторном входе каретка остаётся с позиции прошлой сессии - на кадр
            // мелькает в случайном месте. Ставим строковую позицию в конец (сеттер
            // поднимает m_IsCaretPositionDirty - каретка уедет в конец в первом же
            // Rebuild) и обновляем label синхронно.
            self.screenText.stringPosition = self.screenText.text.Length;
            self.screenText.ForceLabelUpdate();
            if (runner == null) {
                var go = new GameObject("TerminalCursorRunner");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                runner = go.AddComponent<TerminalCursorRunner>();
                mls.LogInfo($"runner created in scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'"); }
       }

       static void ResetCmdRanges() { cmdStarts.Clear(); cmdEnds.Clear(); cmdKeys.Clear(); }

       static void ResetLayoutCache() { lastText = null; lastCharCount = lastVisLines = -1; }

       void RestoreCursorAndInput() { Cursor.lockState = locked; Cursor.visible = wasVisible; StartOfRound.Instance.localPlayerController?.disableLookInput = false; }

       private void Terminal_QuitTerminal(On.Terminal.orig_QuitTerminal orig, Terminal self, bool Use) { orig(self, Use); currentTerminal = null; RestoreCursorAndInput(); }

       // После ЛЮБОГО сабмита (клик по команде или ручной Enter) текст узла
       // уже заменён, а каретка input-поля остаётся в позиции старого текста -
       // на один кадр она мелькает в случайном месте, пока игра не переактивирует
       // поле. Ставим строковую позицию в конец: сеттер поднимает
       // m_IsCaretPositionDirty, и в первом же Rebuild каретка перескочит в конец
       // (MoveTextEnd не годится - он крутит caret-индексы по устаревшему textInfo).
       private void Terminal_OnSubmit(On.Terminal.orig_OnSubmit orig, Terminal t)
       {
           // Пишем ДО orig: после парсинга игра обнуляет textAdded и заменяет
           // текст узла - срезанная строка ввода потерялась бы. Так в историю
           // попадают и неверные команды (пользователь их тоже вводил).
           // Срез последнего ввода (то же, что режет парсер игры) -> в историю.
           // Ничего не вырезаем из ввода: только Trim и нижний регистр.
           // Management commands must not pollute the log.
           // The 'history' command itself must not pollute the log.
           // Banned commands are skipped.
           if (t?.screenText is {} f && t.terminalInUse && t.textAdded > 0 && cfgHistoryEnabled.Value && f.text is { Length: > 0 } z && z[Mathf.Max(0, z.Length - t.textAdded)..].Trim().ToLowerInvariant() is {} a && a[..Math.Min(a.Length, cfgHistoryMaxRawLength.Value)] is { Length: > 0 } key && !("sortpagedelunban" + HistoryWord).Contains(key.IndexOf(' ') is {} s and >= 0 ? key[..s] : key) && !bannedWords.Contains(key)) {
               LoadData();
               if (!history.TryGetValue(key, out var e)) {
                   e = new HistoryEntry();
                   history[key] = e;
                   historyOrder.Add(key);
                   // "all" — виртуальная папка (её содержимое = historyOrder),
                   // отдельная запись в folders["all"] не нужна.
                   // Сверх лимита выкидываем самые редкие (при равных - самые давние).
                   if (history.Count > cfgMax.Value) foreach (var i in history.OrderBy(i => i.Value.Count).ThenBy(i => i.Value.LastTicks).Take(history.Count - cfgMax.Value).ToList()) { history.Remove(i.Key); historyOrder.Remove(i.Key); } }
               e.Count++;
               e.LastTicks = DateTime.UtcNow.Ticks;
               SaveData(); }
           orig(t);
           if (t?.screenText is {} k && t.terminalInUse) k.stringPosition = k.text.Length;
       }

       private void PlayerControllerB_KillPlayer(On.GameNetcodeStuff.PlayerControllerB.orig_KillPlayer orig, PlayerControllerB self, Vector3 bodyVelocity, bool spawnBody, CauseOfDeath causeOfDeath, int deathAnimation, Vector3 positionOffset, bool setOverrideDropItems)
       { orig(self, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset, setOverrideDropItems); if (self.IsLocalPlayer) RestoreCursorAndInput(); }

        // Per-frame logic (driven by our own runner object)
        internal static void Tick()
        { if (currentTerminal is {} t && t.terminalInUse) {
            // Держим каретку input-поля в КОНЦЕ текста каждый кадр, пока терминал
            // открыт. Иначе после смены узла (LoadNewNode) или повторного входа
            // TMP_InputField оставляет строковую позицию от старого текста: каретка
            // мелькает в случайном месте и портит ввод (буквы вставляются не туда /
            // игнорируются). В ваниле позиция всегда в конце - для кликов и вставки
            // команд это ровно то, что нужно (в т.ч. спидраннерам).
            var f = t.screenText;
            // stringPosition (getter = stringSelectPositionInternal) и selectionStringFocusPosition
            // (setter = stringSelectPositionInternal) работают в СТРОКОВЫХ индексах.
            // selectionFocusPosition — это CARET-индекс (в textInfo), его нельзя
            // сравнивать с text.Length.
            if (f?.isFocused == true) {
                int len = f.text.Length;
                if (f.stringPosition != len || f.selectionStringFocusPosition != len) { f.stringPosition = len; f.selectionStringFocusPosition = len; } }
            if (f?.textComponent is not {} tc) return;
            if ((tc.canvas?.worldCamera ?? Camera.main) is not {} cam) return;
            UpdateClickableCache(t);
            // ВАЖНО: ForceMeshUpdate() из мода НЕ зовём - наша принудительная
            // перестройка расходится с игровой (TMP_InputField вставляет символ
            // каретки, индексы textInfo сдвигаются), от этого уезжали и каретка,
            // и подсветка. textInfo всегда актуальна после перестройки меша игрой:
            // она происходит при любом изменении текста и typewriter-анимации.
            TMP_TextInfo ti = tc.textInfo;
            var txt = tc.text;
            // Считаем видимые символы — при typewriter текст появляется побуквенно.
            // isVisible меняется каждый кадр, а characterCount и maxVisibleLines — нет.
            int vis = ti.characterInfo.Take(ti.characterCount).Count(i => i.isVisible);
            if (lastText != txt || lastCharCount != ti.characterCount || lastVisLines != tc.maxVisibleLines || lastVisibleCount != vis) {
                lastText = txt; lastCharCount = ti.characterCount; lastVisLines = tc.maxVisibleLines; lastVisibleCount = vis;
                ResetCmdRanges();
                // На своих страницах (TC_history/TC_bans) текст генерируем сами, и
                // ключи известны точно (customCmds). Матчим их прямым поиском подстроки
                // в тексте: перенос строки (wrap) TMP не мешает, фза "ban <cmd>"
                // подсвечивается целиком даже разорванная на две визуальные строки.
                // Подсветка на страницах мода (TC_*): текст страницы генерируем сами,
                // кликабельные строки known точно (customCmds). Ищем каждую команду как
                // подстроку в тексте и проверяем границы слова (соседи не буква/цифра) —
                // поэтому "ban" внутри "banned" не матчится, а "ban <cmd>" подсвечивается
                // целиком даже когда TMP перенёс строку (у подстроки нет понятия строки).
                if (onCustomNode && !(txt is null or "")) {
                    List<string> keys = [..customCmds];
                    keys.Sort((a, b) => b.Length - a.Length);
                    var claimed = new bool[txt.Length];
                    foreach (var i in keys) {
                        if (i is null or "") continue;
                        int from = 0, idx;
                        while ((idx = txt.IndexOf(i, from, StringComparison.Ordinal)) >= 0) {
                            from = idx + 1;
                            int end = idx + i.Length - 1;
                            if (claimed[idx]) continue;
                            var before = idx > 0 ? txt[idx - 1] : ' ';
                            var after = end + 1 < txt.Length ? txt[end + 1] : ' ';
                            if (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after)) continue;
                            var range = claimed.AsSpan(idx, end - idx + 1);
                            if (range.IndexOf(true) < 0) { range.Fill(true); cmdStarts.Add(idx); cmdEnds.Add(end); cmdKeys.Add(i); }
                } } } else {
                    // Parse display-имена товаров из текста терминала.
                    // Формат: "* Название товара  //  Price: ¤X" или "* Название  //  Цена: ¤X".
                    // Разделитель "//" locale-independent.
                    displayKeyMap.Clear();
                    if (!(txt is null or "")) {
                        // Парсим все строки, содержащие "//" (граница между названием и ценой)
                        int pos = 0, len = txt.Length;
                        while (pos < len) {
                            int nl = txt.IndexOf('\n', pos);
                            if (nl < 0) nl = len;
                            var line = txt[pos..nl];
                            pos = nl + 1;
                            int sl = line.IndexOf("//", StringComparison.Ordinal);
                            if (sl <= 0) continue;
                            var name = line[..sl].Trim();
                            if (name.Length > 0 && name[0] == '*') name = name[1..].Trim();
                            if (name.Length == 0) continue;
                            var norm = Normalize(name);
                            if (norm.Length < 2) continue;
                            // Ищем ключ: сначала точное совпадение, иначе самый длинный ключ-префикс
                            if (keyMap.TryGetValue(norm, out var raw)) displayKeyMap[norm] = raw;
                            else {
                                string best = null, bestRaw = null;
                                foreach (var kv in keyMap) {
                                    if (kv.Key.Length < 3) continue;
                                    if (!norm.StartsWith(kv.Key, StringComparison.Ordinal)) continue;
                                    if (best == null || kv.Key.Length > best.Length) { best = kv.Key; bestRaw = kv.Value; } }
                                if (best != null) displayKeyMap[norm] = bestRaw; } } }
                    var words = new List<(int s, int e, string key)>();
                    int i = 0;
                    while (i < ti.characterCount) {
                        if (!IsWordChar(ti.characterInfo[i])) { i++; continue; }
                        int s = i;
                        while (i < ti.characterCount && IsWordChar(ti.characterInfo[i])) i++;
                        int e = i - 1;
                        var clickedKey = Normalize(BuildWord(ti, s, e));
                        if (clickedKey.Length == 0) continue;
                        words.Add((s, e, clickedKey)); }
                    // 1. Display-имена из текста: "Stun grenade" -> keyMap["stungrenade"]="stun"
                    //    "Signal Translator" -> keyMap["signaltranslator"]="signal"
                    //    "Sofa chair" -> keyMap["sofachair"]="sofa"
                    //    Показываем ВЕСЬ товар целиком, а не только ключевое слово.
                    // 2. N-граммы 1-4 слова: "Zap gun" -> keyMap["zapgun"]="zap gun"
                    //    "Inverse Teleporter" -> keyMap["inverseteleporter"]="inverse teleporter"
                    //    "switch Player 1" -> keyMap["switchplayer1"]="switch Player 1"
                    // 3. Дефисный токен целиком: "TZP-Inhalant" -> сегмент "tzp" -> ключ "tzp",
                    //    но подсвечиваем ВЕСЬ токен (пользователь кликает по любой букве).
                    foreach (var d in new[] {displayKeyMap, keyMap}) {
                        for (int w = 0; w < words.Count; w++) {
                            List<(int, string)> l = [(-1, null)]; var sb = new StringBuilder(); int s = words[w].s;
                            for (int k = w; k < words.Count && k <= w + 3; k++) {
                                if (k > w && ti.characterInfo[words[k - 1].e].lineNumber != ti.characterInfo[s].lineNumber) break;
                                sb.Append(words[k].key);
                                if (!d.TryGetValue($"{sb}", out var raw)) continue;
                                l[0] = (words[k].e, raw);}
                            if (d == keyMap) {
                                l.Add((s, null));
                                var text = BuildWord(ti, s, words[w].e);
                                if (text.Contains('-')) {
                                    int segStart = s;
                                    for (int c = 0; c <= text.Length; c++) {
                                        if (c != text.Length && text[c] != '-') continue;
                                        int segEnd = s + c - 1;
                                        if (segEnd >= segStart) {
                                            var segKey = Normalize(BuildWord(ti, segStart, segEnd));
                                            if (segKey.Length > 0 && d.TryGetValue(segKey, out var raw) && (l[1].Item2 == null || segKey.Length > l[1].Item2.Length)) l[1] = (s, raw); }
                                        segStart = s + c + 1; } } }
                            // Собираем границы всех командных слов текущего текста.
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
                            // Приоритет: для каждой позиции слова берётся САМОЕ ДЛИННОЕ совпадение
                            // с ключом (1-3 слова). Ключи игры бывают с пробелом ("zap gun",
                            // "inverse teleporter", "extension ladder", "hoarding bugs") и с дефисом
                            // ("walkie-talkie"). Кладём ключ в keyMap под НОРМАЛИЗОВАННОЙ формой
                            // (пробел удалён: "zap gun" -> "zapgun"), а храним СЫРУЮ - её шлём в
                            // парсер. Тогда слова экрана "zap"+"gun" дают "zapgun" -> сырой "zap gun".
                            // Дедуп в AddRange не даёт короткому совпадению вытеснить длинное.
                            foreach (var (n, k) in l) { if (k != null && !cmdStarts.Where((t, a) => s <= cmdEnds[a] && n >= t).Any()) { cmdStarts.Add(s); cmdEnds.Add(n); cmdKeys.Add(k); } } } } } }
            int nhs = -1, nhe = -1;
            string key = null;
            if (Mouse.current is {} m) {
                // Экранные пиксели -> пространство рендер-буфера камеры (pixelRect).
                // Нужно из-за пониженного/динамического разрешения: Screen 2560x1440, а
                // камера рендерит в 970x580, и WorldToScreenPoint/FindIntersectingCharacter
                // работают именно в этом буфере.
                var screenPos = m.position.ReadValue();
                var pr = cam.pixelRect;
                var cp = new Vector2(pr.x + screenPos.x * pr.width / Screen.width, pr.y + screenPos.y * pr.height / Screen.height);
                // Индекс символа под экранной точкой - через штатный хит-тест TMP.
                int ra = TMP_TextUtilities.FindIntersectingCharacter(tc, cp, cam, true);
                // Диапазон команды, содержащий указанный символ (N-грамма "Zap gun" и
                // сегмент "TZP" покрывают несколько символов, поэтому ищем по диапазону).
                if (ra >= 0 && ra < tc.textInfo.characterCount) for (int r = 0; r < cmdStarts.Count; r++) if (ra >= cmdStarts[r] && ra <= cmdEnds[r]) { nhs = cmdStarts[r]; nhe = cmdEnds[r]; if (m.leftButton.wasPressedThisFrame) key = cmdKeys[r]; break; } }
            // Перекраска каждый кадр (дешёвая): игра затирает цвета вершин своими
            // перестройками меша, а ForceMeshUpdate мы больше не зовём вслепую.
            // Раскрашивает командные слова: канонические - ярко, прочие - приглушённо,
            // ховер поверх - HoverColor. Остальные символы возвращаются к исходному цвету.
            var it = tc.textInfo;
            for (int i = 0; i < it.characterCount; i++) {
                var ci = it.characterInfo[i];
                if (ci.isVisible) {
                    var c = ci.color;
                    if (nhs >= 0 && i >= nhs && i <= nhe) c = HoverColor;
                    else if (cmdStarts.Where((s, r) => i >= s && i <= cmdEnds[r]).Any()) c = CommandColor;
                    if (it.meshInfo[ci.materialReferenceIndex].colors32 is {} arr) for (int v = ci.vertexIndex; v < ci.vertexIndex + 4;) arr[v++] = c; } }
            tc.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
            // Выполнение команды штатным путём: имитируем ввод слова и жмём OnSubmit.
            // Для команд, требующих аргумент (info, view, TRANSMIT и др.), вместо
            // выполнения просто вводим команду с пробелом в конец текста — пользователь
            // дописывает аргумент и нажимает Enter сам.
            if (!(key is null or "") && f != null) {
                var customNode = t.currentNode && t.currentNode.name.StartsWith("TC_");
                var newText = customNode ? key : t.screenText.text + key; var len = key.Length;
                var log = $"click '{key}' -> node '{(t.currentNode ? t.currentNode.name : "null")}'";
                // Если армим папку (после "page <имя> $") — добавляем команду в папку.
                // Но команды управления (sort, page, del, ban, unban, history) НЕ
                // поглощаются — они должны работать как обычно.
                var armKey = Normalize(key); var flag = 0;
                // Команды управления не поглощаются армом.
                if (armedFolder != "" && !("sortpagedelunban" + HistoryWord).Contains(key.IndexOf(' ') is {} s and >= 0 ? Normalize(key[..s]) : armKey)) {
                    // Возвращаем обновлённую страницу.
                    t.currentNode = BuildHistoryNode(); newText = t.currentNode.displayText; len = 0; flag = 2; log = "";
                    if (armKey.Length > 0 && !folders[armedFolder].Contains(armKey)) { folders[armedFolder].Add(armKey); SaveData(); log = $"added '{armKey}' to folder '{armedFolder}'"; }
                } else armedFolder = ""; // Команда управления — пропускаем дальше (не поглощаем).
                // Команды с обязательным аргументом: вводим текст, не выполняем.
                // Игра не парсит такой глагол один (ParserError), поэтому клик только
                // подставляет "info " в конец поля — пользователь дописывает аргумент
                // и жмёт Enter сам.
                if (flag < 1 && (requiresArg.Contains(armKey) || armKey == "page")) {
                    var typed = key.EndsWith(" ") ? key : key + " ";
                    len = typed.Length; flag = 1;
                    var baseText = armKey == "page" || !customNode ? t.currentText : "";
                    // Повторный клик по той же команде не должен плодить "info info ".
                    if (baseText.EndsWith(typed, (StringComparison)5)) return;
                    newText = baseText + typed; log = log.Replace($"click '{key}", $"type '{typed}");
                    // TC_: текст заменяется целиком (усыхает) — TextChanged игры
                    // при textAdded < 0 вернул бы старый текст узла, поэтому
                    // гасим его modifyingText и ставим всё вручную.
                    // Обычный узел: НЕ трогаем currentText/textAdded руками.
                    // Присвоение text вызывает TextChanged игры, который сам
                    // сделает textAdded += typed.Length и currentText = newTextA.
                    // Только так бэспейс стирает НАБРАННОЕ (в т.ч. несколько
                    // кликов подряд "buy info ") вплоть до текста узла: ручная
                    // подстановка currentText сворачивала ранее вставленное в
                    // "базу" узла, и стереть его было нельзя.
                    if (!customNode && armKey != "page" && t.currentText != newText) len += t.textAdded; }
                // Штатное выполнение команды.
                // На кастомных узлах (TC_) заменяем весь текст командой, не добавляем.
                // Иначе клик на "> switch Player 1" даст "...switch Player 1switch Player 1"
                // ВАЖНО: устанавливаем modifyingText = true, чтобы TextChanged игры
                // не восстановил старый текст узла (он делает screenText.text = currentText
                // когда textAdded < 0, а замена 150→15 символов даёт отрицательную разницу).
                if (customNode && flag < 2) { modFieldCache ??= typeof(Terminal).GetField("modifyingText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); modFieldCache?.SetValue(t, true); };
                // На кадр подстановки гасим поле: каретка не рисуется вообще, а игра
                // в конце OnSubmit сама переактивирует поле уже на новом тексте
                // (ActivateInputField стоит последней строкой в OnSubmit).
                var wasActive = f.isFocused;
                if (wasActive && flag < 1) f.DeactivateInputField();
                // TextChanged сам поднимет textAdded на разницу длин
                // страховка, если onValueChanged не сработал (modifyingText и пр.)
                f.text = t.currentText = newText; t.textAdded = len;
                if (flag > 0) {f.ActivateInputField(); f.stringPosition = f.selectionStringFocusPosition = newText.Length; f.ForceLabelUpdate(); }
                else { t.OnSubmit();
                    // страховка каретки, если поле не был активно и игра её не переактивировала
                    if (!wasActive) { f.stringPosition = t.currentText.Length; f.ForceLabelUpdate(); } }
                // узел сменился: сбрасываем кэш раскладки, чтобы пересобрать на след. кадре
                if (flag < 2) ResetLayoutCache();
                if (log != "") mls.LogInfo(log);
        } } }

        // ===== ФИКС МИГАНИЯ: гармонично пиним каретку в конец при каждом рендере. =====
        // Вызывается из Harmony-патча Patch_OnFillVBO.Prefix (LatePreRender), когда
        // textInfo гарантированно актуален.
        internal static void PinCaretToEnd(TMP_InputField f)
        {
            var ti = f?.textComponent?.textInfo;
            if (ti == null || currentTerminal?.terminalInUse != true || !ReferenceEquals(f, currentTerminal.screenText)) return;
            int caret = Math.Max(0, ti.characterCount - 1), len = f.text.Length;
            // Устанавливаем позиции напрямую через рефлексию, минуя сеттеры
            // (которые делают пересчёт "строка <-> textInfo" по устаревшему textInfo).
            CaretAccess.strPos?.SetValue(f, len);
            CaretAccess.strSelPos?.SetValue(f, len);
            CaretAccess.carPos?.SetValue(f, caret);
            CaretAccess.carSelPos?.SetValue(f, caret);
            // Снимаем флаги пересчёта — не даём OnFillVBO перезаписать позиции
            // из caret-индекса по устаревшему textInfo.
            CaretAccess.strDirty?.SetValue(f, false);
            CaretAccess.carDirty?.SetValue(f, false);
        }

        // Нормализация слова для сравнения с ключевыми словами терминала:
        // оставляем только буквы/цифры и приводим к нижнему регистру. Так снимаются
        // декоративные префиксы терминала ('>MOONS'), кавычки и точки, а также
        // дефисы в названиях (41-Experimentation).
        internal static string Normalize(string s) => string.Concat((s ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
        // --- подсветка через цвета вершин TMP ---
        static readonly Color32 CommandColor = new(90, 200, 255, 255);   // канонические (заголовки/кавычки)
        static readonly Color32 HoverColor = new(255, 214, 0, 255);      // команда под курсором
        static FieldInfo modFieldCache;
        // Кэш ключей: пересобираем при смене узла терминала.
        // Разрешает матчинг фраз через перенос строки - только на наших страницах.
        static bool onCustomNode;
        static void UpdateClickableCache(Terminal t)
        {
            var cn = t.currentNode;
            var nodeKey = cn != null ? cn.name : "<null>";
            // На своих страницах (TC_*) раскладку контролируем сами — разрешаем
            // фразам переноситься через строку (TMP переносит длинные строки,
            // и "ban <cmd>" рвётся, если матчинг требует одну строку).
            onCustomNode = nodeKey.StartsWith("TC_");
            if (nodeKey == cachedNodeKey) return;
            cachedNodeKey = nodeKey;
            keyMap.Clear();
            requiresArg.Clear(); // Сброс: моды могут добавлять/убирать глаголы с compatibleNouns.
            // Кастомные узлы (TC_switch, TC_ping, TC_flash, TC_history, TC_bans) — берём команды из customCmds
            if (onCustomNode) { foreach (var cmd in customCmds) AddKey(cmd); return; }
            // узел принимает всё - ничего не подсвечиваем
            if (cn != null) { if (cn.acceptAnything) return; if (cn.overrideOptions) { foreach (var o in cn.terminalOptions ?? []) if (o?.noun?.word is { Length: > 0 } w) AddKey(w); return; } }
            var list = t.terminalNodes;
            if (list?.allKeywords == null) return;
            foreach (TerminalKeyword kw in list.allKeywords) {
                if (kw?.word is null or "") continue;
                if (kw.isVerb) {
                    // verb+noun пары: "view monitor", "inside cam" и т.д.
                    if (kw.compatibleNouns is { Length: > 0 } o) {
                        // Глагол с совместимыми существительными — требует аргумент:
                        // сам по себе игра не парсит (ParserError). Поэтому кликаем
                        // и его тоже, но клик только ВВОДИТ "info " в поле ввода.
                        // Дедуп в AddRange + самый длинный матчинг N-грамм не дают
                        // одиночному "view" вытеснить фразу "view monitor".
                        requiresArg.Add(Normalize(kw.word));
                        AddKey(kw.word);
                        foreach (var c in o) if (!(c?.noun?.word is null or "") && c.result != null) AddKey(kw.word + " " + c.noun.word); }
                    continue; }
                // одиночное слово работает, если есть глагол по умолчанию
                // (moons/store/...), готовый узел-ответ (help, scan) или это код объекта
                if (kw.defaultVerb != null || kw.specialKeywordResult != null || kw.accessTerminalObjects) AddKey(kw.word); }
            // TRANSMIT — нет в allKeywords Требует [сообщение], поэтому при клике просто вводим "TRANSMIT ".
            // Добавляем ping/flash как кликабельные (их нет в allKeywords, но они есть
            // в хелпе страницы "Other commands" как команды с аргументами).
            // history — команда мода: подсвечиваем, если слово встретилось в тексте
            // (страницы модов-переводчиков/хоелп-аддонов).
            // del/remove — сокращения для delete (минимум 3 символа)
            // ban/unban — страницы банов
            // page — навигация по папкам (требует аргумент: имя папки или $)
            foreach (var w in (string[])[..Patch_ParsePlayerSentence.cmds, "TRANSMIT", "ping", "flash", HistoryWord]) AddKey(w);
            requiresArg.Add("transmit");
            requiresArg.Add("page");
        }

        // Ключ кладём под нормализованной формой (для сверки со словами экрана),
        // но храним СЫРОЕ kw.word - его отправляем в парсер игры.
        static void AddKey(string word)
        {
            var norm = Normalize(word);
            if (norm.Length == 0) return;
            // при коллизии нормализаций ("zap gun"/"zapgun") предпочтительнее
            // тот ключ, что парсится точной фразой, - с пробелом
            if (keyMap.TryGetValue(norm, out var old) && word.IndexOf(' ') < 0 && old.IndexOf(' ') >= 0) return;
            keyMap[norm] = word;
        }

        private static bool IsWordChar(TMP_CharacterInfo ci) => ci.isVisible && !char.IsWhiteSpace(ci.character);

        // Команды, требующие аргумент: при клике не выполнять, а вводить текст
        // в терминал (например "info ", "TRANSMIT "). Нормализованные (без пробелов,
        // нижний регистр), как они хранятся в keyMap.
        static readonly HashSet<string> requiresArg = [];

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

        // Получить отфильтрованный список целей для команды.
        // Возвращает массив (idx, name, isNonPlayer) для совместимости.
        // switch: connected players + radars.
        // ping/flash: только radars (isNonPlayer).
        internal static (int idx, string name, bool isNonPlayer)[] GetValidTargets(string cmd)
        {
            List<(int idx, string name, bool isNonPlayer)> list = [];
            if (StartOfRound.Instance?.mapScreen is not {} map) return [];
            var targetsField = map.GetType().GetField("radarTargets", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetsField?.GetValue(map) is not System.Collections.IList radarTargets || radarTargets.Count == 0) return [];
            // Кэшируем reflection-поля первого элемента
            if (fiTaName == null) {
                var t = radarTargets[0].GetType();
                fiTaName = t.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                fiTaIsNonPlayer = t.GetField("isNonPlayer", BindingFlags.Public | BindingFlags.Instance);
                fiTaTransform = t.GetField("transform", BindingFlags.Public | BindingFlags.Instance); }
            // Карта: thisPlayerBody → PlayerControllerB (только подключённые)
            Dictionary<Transform, PlayerControllerB> connectedBodies = [];
            foreach (var p in StartOfRound.Instance?.allPlayerScripts ?? []) if (p?.thisPlayerBody != null && p.isPlayerControlled) connectedBodies[p.thisPlayerBody] = p;
            for (int i = 0; i < radarTargets.Count; i++) {
                var target = radarTargets[i];
                // Убираем '#' (RemovePunctuation игры его снимает, но # ломает textAdded)
                if (((string)fiTaName?.GetValue(target))?.Replace("#", "").Trim() is not { Length: > 0 } name) continue;
                var isNonPlayer = (bool)(fiTaIsNonPlayer?.GetValue(target) ?? false);
                var targetTransform = (Transform)fiTaTransform?.GetValue(target);
                // Для switch: пропускаем пустые слоты (не подключённые игроки)
                if (!isNonPlayer && !connectedBodies.ContainsKey(targetTransform)) continue;
                // Для ping/flash: только радары (isNonPlayer)
                if (cmd == "switch" || isNonPlayer) list.Add((i, name, isNonPlayer)); }
            return [..list];
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
            var entries = GetValidTargets(cmd);
            // ping/flash без радаров → показать страницу товара Radar-booster
            // (buy radar -> compatibleNouns.defaultVerb -> result)
            if (entries.Length < 1) t.screenText.text = t.currentText[..^cmd.Length] + "radar";
            // 1 цель → выполнить напрямую (действуем сразу, возвращаем success-ноду)
            if (entries.Length < 2) return entries.Length > 0 ? ExecuteTargetCommandWithIndex(t, cmd, entries[0].idx) : null;
            // > 1 цели → строим список
            var sb = new StringBuilder();
            // ПУНКТ 1: берём заголовок ИЗ ИГРЫ — RTLC/XUnity уже знает эти строки
            // и переведёт их автоматически при рендере (intercepts TMP_Text.text setter).
            // specialNodes[20] = switch success node, [21] = ping, [23] = flash
            var specialNodes = t.terminalNodes?.specialNodes;
            var cmdId = PlayerCmds[cmd].idx;
            sb.AppendLine(specialNodes?.Count > cmdId && specialNodes[cmdId].displayText is { Length: > 0 } c ? c : (cmd == "switch" ? cfgHeaderSwitch : cmd == "ping" ? cfgHeaderPing : cfgHeaderFlash).Value);
            sb.AppendLine();
            customCmds.Clear();
            int curIdx = -1;
            var map = StartOfRound.Instance?.mapScreen;
            var targetIdxField = map?.GetType().GetField("targetTransformIndex", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetIdxField?.GetValue(map) is int i) curIdx = i;
            foreach (var (idx, name, isNonPlayer) in entries) {
                // Команда БЕЗ маркеров — для кликабельности (parser expects "switch Player 0")
                var rawLine = cmd + " " + name;
                customCmds.Add(rawLine);
                // ПУНКТ 3: пометка (radar) только для switch (ping/flash и так только радары)
                // Текущая цель помечена (current)
                sb.AppendLine(">" + rawLine + (cmd == "switch" && isNonPlayer ? "  " + cfgMarkRadar.Value : "") + (idx == curIdx ? "  " + cfgMarkCurrent.Value : "")); }
            return MakeNode(cmd, sb);
        }

        static TerminalNode MakeNode(string name, StringBuilder text)
        {
            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.name = "TC_" + name; node.displayText = $"{text}"; node.clearPreviousText = true;
            if (lastCustomNode != null) Destroy(lastCustomNode);
            lastCustomNode = node; cachedNodeKey = null; return node;
        }

        // Выполнение команды switch/ping/flash с аргументом (цель по индексу).
        // Вызывается из Harmony-патча ParsePlayerSentence или BuildTargetListNodeInternal.
        internal static TerminalNode ExecuteTargetCommandWithIndex(Terminal t, string cmd, int targetIdx)
        {
            if (StartOfRound.Instance?.mapScreen is not {} map) return null;
            // Вызываем метод MapScreen напрямую
            var (methodName, specialIdx) = PlayerCmds[cmd];
            try {map.GetType().GetMethod(methodName)?.Invoke(map, [targetIdx]);}
            catch (Exception ex) {mls.LogWarning($"[TC] {methodName} failed: {ex.Message}");}
            // Возвращаем game-специальную ноду (сообщение об успехе)
            return t.terminalNodes?.specialNodes?.ElementAtOrDefault(specialIdx);
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
        static ConfigEntry<int> cfgMax, cfgHistoryMaxRawLength; // Защита от мусора: длина строки в истории
        static ConfigEntry<string> cfgHistoryWord, cfgHistoryHeader, cfgHistoryEmpty;
        static ConfigEntry<string> cfgPageNavLine, cfgFoldersBtn, cfgPageNavTemplate;
        static ConfigEntry<string> cfgHistoryDateFormat, cfgHistoryLineFormat;
        static ConfigEntry<string> cfgBannedLine, cfgBanHeader, cfgBanEmpty, cfgBanLineFormat;
        static ConfigEntry<string> cfgSortArrowAsc, cfgSortArrowDesc;
        static ConfigEntry<string> cfgSortLine, cfgSortCurrent;
        static ConfigEntry<string> cfgCreateFolderLine, cfgBackToHistoryLine;

        static readonly Dictionary<string, HistoryEntry> history = [with(StringComparer.Ordinal)];
        static bool historyLoaded;
        static string dataFile;

        // Порядок ключей = порядок их записи в JSON ("e"). Даёт фильтр
        // "sort time" (первым был использован тот, что в файле выше).
        // Пишем файл строго в этом порядке, поэтому порядок НЕ зависит от
        // внутреннего обхода Dictionary (который после удаления ключа мог бы
        // поставить новый ключ в освободившийся слот, то есть в середину).
        static readonly List<string> historyOrder = [];

        // Режим и направление сортировки страницы истории. Живёт пока открыт
        // терминал; "sort <режим>" переключает режим, повторный клик по тому же
        // режиму разворачивает направление.
        internal enum HistorySort { Count, Date, Alphabet, Time }
        static HistorySort historySort = HistorySort.Count;
        // Направление по умолчанию: alphabet/time — ↑ (A-Z, сначала старые),
        // count/date — ↓ (чаще/свежего выше).
        static bool historySortAsc;

        // Забаненные нормализованные команды — не попадают в историю.
        static readonly HashSet<string> bannedWords = [with(StringComparer.Ordinal)];

        // Папки команд. Ключ = имя папки, значение = список команд.
        // "all" — всегда есть, содержит все команды с метаданными.
        static readonly Dictionary<string, List<string>> folders = [with(StringComparer.Ordinal)];
        static string currentFolder = "all"; // Текущая открытая папка
        static string armedFolder = ""; // Папка для добавления команды (после "page <имя> $")

        class HistoryEntry { public int Count; public long LastTicks; }

        // Нормализованное слово команды истории (для перехвата и подсветки).
        internal static string HistoryWord => Normalize(cfgHistoryWord.Value);

        // Сортировка списка записей по текущему режиму (historySort / historySortAsc).
        // Собираем пары из текущей папки ("all" = вся история) и сортируем по активному режиму —
        // тот же порядок, что показывает страница, поэтому "del 2" == клик
        // по "del" во второй строке.
        // Содержимое папки для показа. "all" — виртуальная: все известные команды
        // в порядке первой регистрации (historyOrder).
        // Применяем текущий режим сортировки.
        static List<KeyValuePair<string, HistoryEntry>> sorted;
        static void GetSortedFolderEntries()
        {
             sorted = [..(currentFolder == "all" ? historyOrder : folders.GetValueOrDefault(currentFolder) ?? []).Where(history.ContainsKey).Select(c => new KeyValuePair<string, HistoryEntry>(c, history[c]))];
             var orderMap = historySort == HistorySort.Time ? historyOrder.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal) : null;
             sorted.Sort((a, b) => { int c = historySort switch { HistorySort.Count => a.Value.Count.CompareTo(b.Value.Count), HistorySort.Date => a.Value.LastTicks.CompareTo(b.Value.LastTicks), HistorySort.Alphabet => string.CompareOrdinal(a.Key, b.Key), HistorySort.Time => orderMap.TryGetValue(a.Key, out int ia) && orderMap.TryGetValue(b.Key, out int ib) ? ia.CompareTo(ib) : 0, _ => 0 }; return c != 0 ? historySortAsc ? c : -c : string.CompareOrdinal(a.Key, b.Key); });
        }

        // Динамический узел TC_history: строки
        // "№N > <команда>  [N, дата]  del N | ban N".
        // Клик по команде выполняет её; клик по "del N" / "ban N" удаляет /
        // банит её и сразу перерисовывает страницу (хендлеры возвращают узел).
        static readonly (HistorySort mode, string name)[] SortModes = [(HistorySort.Alphabet, "alphabet"), (HistorySort.Count, "count"), (HistorySort.Date, "date"), (HistorySort.Time, "time")];
        internal static TerminalNode BuildHistoryNode()
        {
            LoadData();
            GetSortedFolderEntries();
            customCmds.Clear();
            customCmds.Add("page");  // всегда кликабельно для строки "create folder"
            var sb = new StringBuilder();
            sb.AppendLine(cfgHistoryHeader.Value);
            sb.AppendLine();
            if (bannedWords.Count > 0) { sb.AppendLine(SafeFormat(cfgBannedLine, bannedWords.Count)); sb.AppendLine(); }
            // Строка фильтров сортировки — кликабельна.
            // Кнопки сортировки: строка кнопок + строка текущего режима.
            // Кнопка показывает направление, которое БУДЕТ применено при клике
            // (для активного режима — противоположное текущему).
            // Клик по активному режиму разворачивает текущий; по неактивному — включает режим с его направлением по умолчанию.
            sb.AppendLine(SafeFormat(cfgSortLine, string.Join(" | ", SortModes.Select(m => $"sort {m.name} {((m.mode == historySort ? !historySortAsc : m.mode is HistorySort.Alphabet or HistorySort.Time) ? cfgSortArrowAsc : cfgSortArrowDesc).Value}"))));
            foreach (var m in SortModes) customCmds.Add("sort " + m.name);
            // Текущий режим — БЕЗ слова "sort", чтобы матчинг по подстроке не
            // сделал эту строку кликабельной (по условию она не кликабельна).
            sb.AppendLine(SafeFormat(cfgSortCurrent, $"{historySort}".ToLowerInvariant(), (historySortAsc ? cfgSortArrowAsc : cfgSortArrowDesc).Value));
            sb.AppendLine();
            // Строка навигации между папками: "page store | page tools"
            // + строка-шаблон "page <current> $" (кроме all).
            // Все папки, кроме текущей.
            List<string> btns = ["all", ..folders.Keys];
            btns.Remove(currentFolder);
            if (btns.Count > 0) {
                // "page {0}" not "page {0} [{1}]"
                static string T(string s, int n) => n < 1 && s.Contains("{1}") && s.Contains(" ") ? s[..s.LastIndexOf(' ')] : s;
                List<string> navBtns = [];
                foreach (var f in btns) {
                    var n = f == "all" ? history.Count : folders[f].Count;
                    navBtns.Add(SafeFormat(T(cfgFoldersBtn.Value, n), T((string)cfgFoldersBtn.DefaultValue, n), n < 1 ? [f] : [f, n]));
                    customCmds.Add("page " + f); }
                sb.AppendLine(SafeFormat(cfgPageNavLine, string.Join(" | ", navBtns))); }
            // Шаблон: "page <current> $" — кликабельный (кроме all).
            if (currentFolder != "all") { sb.AppendLine(SafeFormat(cfgPageNavTemplate, currentFolder)); customCmds.Add($"page {currentFolder} $"); }
            // Подсказка о создании папки.
            sb.AppendLine(cfgCreateFolderLine.Value);
            sb.AppendLine();
            // Уведомление об арме (добавление в папку).
            if (armedFolder != "") { sb.AppendLine($"click a command to add it to folder '{armedFolder}'"); sb.AppendLine(); }
            if (sorted.Count == 0) sb.AppendLine(cfgHistoryEmpty.Value);
            else {
                // Длина номера для выравнивания: "№10 " vs "№1  "
                int n = 0, numWidth = $"{sorted.Count} ".Length;
                foreach (var i in sorted){
                    string date = "", key = i.Key, num = $"{++n}", cnt = $"{i.Value.Count}";
                    // Для команд — только число.
                    customCmds.Add(key);
                    customCmds.Add("del " + num);
                    customCmds.Add("ban " + num);
                    try{date = new DateTime(i.Value.LastTicks, (DateTimeKind)1).ToString(cfgHistoryDateFormat.Value);}catch(FormatException){}
                    // Одна строка: если TMP перенесёт её на вторую — не страшно,
                    // на TC_ страницах матчинг идёт по подстроке всего текста.
                    // {3} — номер с № и выравниванием (левый столбец),
                    // {4} — чистый номер: он же лежит в customCmds, поэтому
                    // кнопки "del 1"/"ban 1" матчатся и кликабельны.
                    // PadRight Для отображения — с символом и пробелами.
                    sb.AppendLine(SafeFormat(cfgHistoryLineFormat, key, cnt, date, num.PadRight(numWidth), num)); } }
            // Слово 'ban' из строки "banned [N] (open ban list)" должно быть
            // кликабельным -> страница банов. keyMap на TC_ нодах строится только
            // из customCmds, поэтому добавляем его сюда.
            customCmds.Add("ban");
            return MakeNode("history", sb);
        }

        // Динамический узел TC_bans: список забаненных команд.
        // Клик по "unban <команда>" разбанивает и перерисовывает список.
        internal static TerminalNode BuildBanListNode()
        {
            LoadData();
            customCmds.Clear();
            var sb = new StringBuilder();
            sb.AppendLine(cfgBanHeader.Value);
            sb.AppendLine();
            // Возврат к истории — вверху списка, кликабельно.
            customCmds.Add(HistoryWord);
            sb.AppendLine(SafeFormat(cfgBackToHistoryLine, HistoryWord, history.Count));
            sb.AppendLine();
            if (bannedWords.Count == 0) sb.AppendLine(cfgBanEmpty.Value);
            // Все баны на одной странице
            // Длина номера для выравнивания
            else {
                List<string> list = [..bannedWords];
                list.Sort(StringComparer.Ordinal);
                int n = 0, numWidth = $"{list.Count} ".Length;
                foreach (var w in list) { customCmds.Add("unban " + w); sb.AppendLine(SafeFormat(cfgBanLineFormat, w, $"{++n}".PadRight(numWidth))); } }
            return MakeNode("bans", sb);
        }

        internal static TerminalNode HandleSort(string a)
        {
            if (!Enum.TryParse<HistorySort>(a, true, out var mode) || !Enum.IsDefined(typeof(HistorySort), mode)) return BuildHistoryNode();
            historySortAsc = mode == historySort ? !historySortAsc : mode is HistorySort.Alphabet or HistorySort.Time;
            historySort = mode;
            mls.LogInfo($"sort -> {mode} {(historySortAsc ? "a" : "de")}sc");
            return BuildHistoryNode();
        }

        // page <имя> — открыть/создать папку.
        // page <имя> $ — армить добавление команды в папку (возвращает в all).
        // page all — вернуться к полной истории.
        internal static TerminalNode HandlePage(string a)
        {
            LoadData();
            mls.LogInfo($"HandlePage a='{a}'");
            // Спецсимвол '$' в конце — арм добавления.
            var arm = a.EndsWith("$");
            if (arm) a = a[..^1].Trim();
            if (a.Length == 0) return BuildHistoryNode();
            // "all" — виртуальная папка: открывается, но не создаётся и не армится.
            var notAll = a != "all";
            if (notAll && folders.TryAdd(a, [])) SaveData();
            currentFolder = arm && notAll ? "all" : a;
            armedFolder = arm && notAll ? a : "";
            mls.LogInfo($"{(arm ? "armed" : "opened")}{(notAll ? "" : "folder")}: '{a}'");
            return BuildHistoryNode();
        }

        // string.Format с падением на безопасный вариант (конфиг правят руками).
        static string SafeFormat(ConfigEntry<string> fmt, params object[] args) => SafeFormat(fmt.Value, (string)fmt.DefaultValue, args);
        static string SafeFormat(string Value, string DefaultValue, params object[] args)
        { try { return string.Format(Value, args); } catch (FormatException) { return string.Format(DefaultValue, args); } }

        static void LoadData()
        {
            if (historyLoaded || dataFile is null or "" || !File.Exists(dataFile)) {historyLoaded = true; return; }
            historyLoaded = true;
            try { foreach (var n in JObject.Parse(File.ReadAllText(dataFile, Encoding.UTF8))) foreach (var k in n.Value) { if (k is JProperty i) {
                // Загружаем "e" — словарь пользовательских папок.
                // "all" — виртуальная (из historyOrder), не храним и не грузим из "e".
                if (n.Key == "e" && i.Name != "all") folders[i.Name] = i.Value.ToObject<List<string>>();
                // Загружаем "all" — полноценный словарь с метаданными.
                if (n.Key == "all") { history[i.Name] = new HistoryEntry { Count = Math.Max(1, (int)i.Value["c"]), LastTicks = (long)i.Value["t"] }; if (!historyOrder.Contains(i.Name)) historyOrder.Add(i.Name); } }
                if (n.Key == "b") bannedWords.Add(((string)k).ToLowerInvariant());}
             } catch (Exception ex) { mls.LogWarning("data: failed to read file: " + ex.Message); }
        }

        static void SaveData()
        {
            if (dataFile is null or "") return;
            try {
                var all = new JObject();
                // Пишем "all" — полноценный словарь с метаданными.
                foreach (var k in historyOrder) if (history.TryGetValue(k, out var e)) all[k] = new JObject{["c"] = e.Count, ["t"] = e.LastTicks};
                // Страховка: ключ есть в словаре, но потерялся в порядке.
                foreach (var kv in history) all[kv.Key] ??= new JObject{["c"] = kv.Value.Count, ["t"] = kv.Value.LastTicks};
                // Пишем только пользовательские папки: "all" — виртуальная
                // (содержимое = historyOrder), дублировать её в "e" не нужно.
                var data = new JObject{["v"] = 2, ["all"] = all, ["e"] = new JObject(folders.Where(f => f.Key != "all").Select(f => new JProperty(f.Key, new JArray(f.Value)))), ["b"] = new JArray(bannedWords)};
                File.WriteAllText(dataFile, $"{data:None}", new UTF8Encoding(false));
            } catch (Exception ex) { mls.LogWarning("data: failed to write file: " + ex.Message); }
        }

        // Команду: del <cmd> — удалить из истории,
        // ban <cmd> — запретить (удаляет из истории сразу, перестанет
        // записываться в дальнейшем).
        // Возвращают свежую страницу (history/список банов) — терминал
        // сразу показывает обновлённый список.
        // Аргумент может быть номером строки ("2") или самой командой.
        // flag (del = 0, unban = 1, ban = 2)
        // ban удаляет команду из истории и запрещает ей появляться в дальнейшем.
        internal static TerminalNode HandleDelete(string a, int flag = 0)
        {
            LoadData(); var delete = "not found"; var doSave = true; var flag0 = flag > 0; var flag1 = flag > 1;
            a = a[..Math.Min(a.Length, cfgHistoryMaxRawLength.Value)];
            // Аргумент del/ban/unban: либо номер строки истории ("2"), либо сама
            // команда ("buy shovel"). Номер резолвится по той же сортировке, что и
            // страница, поэтому "del 2" == клик по "del" во второй строке.
            if (int.TryParse(a, out int n) && n > 0) { GetSortedFolderEntries(); if (n <= sorted.Count) a = sorted[--n].Key; }
            if (a.Length > 0) {
                if (flag0) {doSave = flag1 ? bannedWords.Add(a) : bannedWords.Remove(a); mls.LogInfo($"{(flag1 ? "" : "un")}ban '{a}' ->{(doSave ? "" : (flag1 ? " already" : " not"))} banned"); }
                // Удаляем из всех папок и из all.
                if (flag1 || (currentFolder == "all" && !flag0)) { history.Remove(a); historyOrder.Remove(a); foreach (var folder in folders.Values) folder.Remove(a); }
                // Удаляем только из текущей папки.
                else if (!flag0 && folders.ContainsKey(currentFolder)) folders[currentFolder].Remove(a);
                if (doSave) SaveData(); delete = "removed"; }
            if (!flag0) mls.LogInfo($"delete '{a}' -> " + delete);
            return flag0 ? BuildBanListNode() : BuildHistoryNode();
        }

        private static string BuildWord(TMP_TextInfo ti, int start, int end) => new([..ti.characterInfo[start..++end].Select(k => k.character)]);
    }


    /// <summary>
    ///   Свой per-frame runner: GameObject самого плагина игра уничтожает при
    ///   загрузке первой сцены, поэтому Update плагина не вызывается. Этот объект
    ///   создаётся уже при загруженной сцене и живёт дальше.
    /// </summary>
    public class TerminalCursorRunner : MonoBehaviour { private void Update() => TerminalCursorPlugin.Tick(); }

    // =====================================================================
    //  Доступ к приватным полям TMP_InputField, отвечающим за каретку и
    //  выделение. Нужен потому, что публичные сеттеры (caretPosition /
    //  stringPosition) пересчитывают "строковый индекс <-> индекс в textInfo"
    //  через textInfo, который в момент вызова может быть устаревшим.
    // =====================================================================
    internal static class CaretAccess
    {
        internal static FieldInfo strPos, strSelPos, carPos, carSelPos, strDirty, carDirty;
        internal static FieldInfo F(string t) => typeof(TMP_InputField).GetField("m_" + t, BindingFlags.Instance | BindingFlags.NonPublic);
        internal static void Init() {
            strPos = F("StringPosition"); strSelPos = F("StringSelectPosition");  // индекс в строке
            carPos = F("CaretPosition"); carSelPos = F("CaretSelectPosition");  // индекс в textInfo
            strDirty = F("IsStringPositionDirty"); carDirty = F("IsCaretPositionDirty"); } // флаги пересчёта позиций
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
        internal static readonly string[] cmds = "del unban ban sort page".Split();
        internal static readonly string[] methods  = "HandleDelete HandleSort HandlePage".Split();
        static MethodInfo removePunct;

        // CallMethodCursor for TerminalCursorPlugin.
        static TerminalNode CallMethodCursor(params object[] args) => (TerminalNode)typeof(TerminalCursorPlugin).GetMethod((string)args[0], BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args[1..]);

        static bool Prefix(Terminal __instance, ref TerminalNode __result)
        {
            // Не трогаем узлы с overrideOptions/acceptAnything (confirm/deny и т.д.)
            if (__instance.currentNode is {} c && (c.overrideOptions || c.acceptAnything)) return true;
            if (__instance.screenText == null || __instance.textAdded < 1) return true;
            var s = __instance.screenText.text[Mathf.Max(0, __instance.screenText.text.Length - __instance.textAdded)..];
            // Сырой срез ДО RemovePunctuation: игра снимает пунктуацию, а спецсимвол
            // '$' (арм добавления в папку) нужен нам целым.
            var raw = s;
            // Вызываем RemovePunctuation игры (приватный метод)
            removePunct ??= AccessTools.Method(typeof(Terminal), "RemovePunctuation");
            if (removePunct != null) s = (string)removePunct.Invoke(__instance, [s]);
            // DEBUG: лог состояния
            if (s.Split(' ', (StringSplitOptions)1) is not { Length: > 0 } parts) return true;
            var cmd = parts[0].ToLowerInvariant();
            var isPlayerCmd = TerminalCursorPlugin.PlayerCmds.ContainsKey(cmd);
            if (parts.Length > 1) {
                // Команда с аргументом (например "switch player 1")
                // Пропускаем сломанный CheckForPlayerNameCommand (матчит только array[1])
                // и вызываем MapScreen-методы напрямую
                if (isPlayerCmd) {
                    // Собираем аргумент: всё после команды
                    var arg = s[cmd.Length..].Trim();
                    // Ищем индекс цели по аргументу
                    var entries = TerminalCursorPlugin.GetValidTargets(cmd);
                    int foundIdx = -1; var normArg = TerminalCursorPlugin.Normalize(arg);
                    foreach (var e in entries) if (TerminalCursorPlugin.Normalize(e.name.Replace("#", "")) == normArg) { foundIdx = e.idx; break; }
                    if (foundIdx >= 0) { __result = TerminalCursorPlugin.ExecuteTargetCommandWithIndex(__instance, cmd, foundIdx); return false; } }
                // del <cmd> / ban <cmd> / unban <cmd> /
                // sort <mode> — управление историей.
                int m_idx = Array.IndexOf(cmds, cmd);
                if (m_idx < 0) return true;
                // m_idx: HandleDelete index 0 (cmd in del unban ban), у осттально index начинается с 1
                __result = CallMethodCursor(((object[])[methods[m_idx < 3 ? 0 : m_idx - 2], (cmd == "page" ? raw : s)[parts[0].Length..].Trim().ToLowerInvariant(), m_idx])[..^(m_idx > 2 ? 1 : 0)]);
                return false; }
            // null (0 или 1 цель) → пусть игра обработает ванильно
            if (isPlayerCmd) {__result = TerminalCursorPlugin.BuildTargetListNode(__instance, cmd); return __result == null;}
            // history: команда наша (у игры её нет) - показываем страницу истории.
            // ban/unban без аргумента -> страница со списком забаненных команд.
            // del без аргумента -> обновлённая страница истории.
            // sort без аргумента -> обновлённая страница с тем же режимом сортировки.
            // Слово уже есть у игры (ванилла или другой мод)? Не перехватываем.
            var build = cmd == "sort" || ((cmd == TerminalCursorPlugin.HistoryWord || cmds.Contains(cmd)) && __instance?.terminalNodes?.allKeywords?.Any(i => i && TerminalCursorPlugin.Normalize(i.word) == cmd) != true);
            if (build) __result = CallMethodCursor("unban".Contains(cmd) ? "BuildBanListNode" : "BuildHistoryNode");
            return !build;
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