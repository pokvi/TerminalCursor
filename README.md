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

### 1.0.2
* Commands requiring an argument (info, view, route, buy, TRANSMIT, etc.) now
  type the command with a trailing space into the input field instead of
  executing it — the user finishes the argument and presses Enter themselves.
  This prevents ParserError that the vanilla game gives when such a verb is
  entered alone.
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