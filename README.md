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

### 1.0.0
* Locked camera when in terminal
* Fixed vanilla blinking caret (see Bug fixes)
* Command highlighting: store items (multi-word names, hyphenated names,
  display names like "Stun grenade"), verb+noun commands ("view monitor")
* Clickable commands: click a highlighted word to run it