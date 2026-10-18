# TerminalCursor
A mod which enables mouse cursor while player is using Terminal

# Bug fixes

### Blinking caret in a random place (vanilla game bug)
This is NOT a mod bug - it happens in the unmodded game too (seen in
vanilla playthrough videos: after a new terminal page is printed, the
text caret briefly jumps to a random position in the middle of the text,
e.g. right after a word on the store page).

Root cause (decompiled Terminal.cs): `Terminal.Update()` assigns
`screenText.caretPosition = text.Length` every frame. But `caretPosition`
is an index into `textInfo` (character count), not a string offset. On the
frame where node text was already replaced but the TMP mesh was not yet
rebuilt, `characterCount` still refers to the OLD layout. The setter clamps
the value to the old `characterCount - 1` and marks string position dirty.
`TMP_InputField.OnFillVBO` then recalculates the caret's string position
from this wrong index - for one frame the caret is drawn at the middle of
the new text.

This mod fixes it with a Harmony prefix on `TMP_InputField.OnFillVBO` that
writes the correct caret/string positions and clears the dirty flags, so
the caret always stays at the end of the text.

# Releases

### 1.1.0
* **Command history.** Typing `history` (or clicking it) opens a page with every
  command ever typed or clicked in the terminal, with a usage counter and the
  last-used date:
  `№1  >store  [16, 18.10.2026]  del 1 | ban 1`
  Line numbers are padded so the `>` column always lines up. Clicking the
  command itself runs it; `del <n>` / `ban <n>` remove or block it. Both handlers
  re-render the page immediately, so the list is always current.
* **Storage.** History and the ban list share one file,
  `BepInEx/config/TerminalCursor.json` — one per game installation, so counters
  are global across all servers and saves and survive restarts. Written as a
  compact JSON object: `{"v":1,"e":{"<command>":{"c":uses,"t":ticks}},"b":[...]}`.
  Keys are written in order of first registration, which is what the `time`
  sort uses. No third-party JSON library is bundled (the whole mod stays ~40 KB).
* **Ban list.** `ban <cmd>` blocks a command: it is removed from history and will
  not be recorded again. `ban` / `unban` with no argument opens the ban list,
  where every line `№1  >unban buy` unbans on click, with a `>history` link back.
* **Sorting.** The history page has a clickable filter row:
  `sort: sort alphabet ↑ | sort count ↓ | sort date ↓ | sort time ↑`
  plus a non-clickable `current: count ↓` line. Clicking a mode switches to it
  with its natural direction (alphabet/time ascending, count/date descending);
  clicking the active mode again reverses it. `time` follows the order of keys
  in the JSON file, i.e. order of first use.
* **Management aliases.** `delete`, `del`, `remove`, `rem` all remove a command
  (3+ letters, matching the game's own abbreviation style). The argument is
  either a line number (`del 2`) or the command itself (`del buy shovel`).
  Management commands are never recorded into the history themselves.
* **Long / wrapped commands stay clickable.** On the mod's own pages
  (`TC_history`, `TC_bans`) commands are matched by plain substring search in the
  whole node text instead of per-line N-grams, so a command that TextMeshPro
  wraps onto a second visual line is still highlighted and clickable as one
  piece. Vanilla pages keep the stricter line-based matching to avoid false hits.
* **Highlighting no longer flickers.** The highlight cache is rebuilt when the
  count of *visible* characters changes, not only on text/line-count changes, so
  the typewriter animation no longer drops highlights mid-reveal.
* All new text (headers, line templates, arrows, sort labels) lives in BepInEx
  `ConfigEntry` values, so any translator mod (RTLC, XUnity, ...) can localise it
  by editing `BepInEx/config/TerminalCursor.cfg`.

### 1.0.2
* Commands requiring an argument (info, view, route, buy, TRANSMIT, etc.) now
  type the command with a trailing space into the input field instead of
  executing it — the user finishes the argument and presses Enter themselves.
  This prevents ParserError that the vanilla game gives when such a verb is
  entered alone.
* The typed text is fully erasable with Backspace (all the way back to the
  node text). Implemented by letting the game's own `Terminal.TextChanged`
  track `textAdded`/`currentText` — setting them manually folded previously
  inserted words into the node "baseline" and Backspace could not remove them.
* The verb itself (e.g. "info") is now clickable alongside full phrases
  ("info shovel", "view monitor"). Double-clicking the same command is
  ignored to avoid "info info " duplication.
* Works with mod-added commands: any verb registered in `allKeywords` with
  `isVerb = true` and a non-empty `compatibleNouns` list is automatically
  treated as "requires argument".

### 1.0.1
* Clickable switch/ping/flash target list pages: typing "switch" (or clicking
  it) opens a dynamic page with all radar targets (connected players + radar
  boosters). Each line is clickable — click "> switch Player 0" to switch
  directly to that target.
* Filtering: empty player slots (Player 1-3 who aren't playing) are hidden.
  Only connected players (isPlayerControlled + thisPlayerBody) are shown.
* Radar mark: non-player targets on the switch page are labelled "(radar)".
  Current target is labelled "(current)".
* ping/flash without radars: shows the Radar-booster item page (buy radar)
  instead of a blank screen or ParserError.
* Direct execution: 1 target → execute immediately (no list page).
  2+ targets → show the clickable list.
* RTLС-compatible: header text and labels stored in BepInEx ConfigEntry.
  Any translator mod (RTLC, XUnity, etc.) reads .cfg and overwrites values.

### 1.0.0
* Locked camera when in terminal
* Fixed vanilla blinking caret (see Bug fixes)
* Command highlighting: store items (multi-word names, hyphenated names,
  display names like "Stun grenade"), verb+noun commands ("view monitor")
* Clickable commands: click a highlighted word to run it