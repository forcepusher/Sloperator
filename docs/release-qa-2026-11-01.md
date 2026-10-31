# Release check, 1 November 2026

A last pass before 1.3.0, branch `release/1.3.0` off develop 36ae5f8: a clean-up of what changed since
1.2.0, the unit suites, the new panels and the rest of this version's changes driven in the published exe
on the laptop, a Unity 2048 run as a sanity check of the agent, and on the workstation what needs its
programs, its GPU, its second monitor or the Qwen server.

## In short

| Check | Where | Result |
|---|---|---|
| Unit suites | laptop | App 285 of 285, Core 1220 of 1220 (20 opt-in skipped), after the clean-up and again after the fixes |
| Unit suites with the opt-in tests on | workstation | App 284 of 285, Core 1220 of 1231 at first: none a product bug, three fixed (below) |
| Unity 2048 (`unity2048-test.ps1`, Claude Haiku 5.5 through OpenRouter) | laptop | pass in 12 min 51 s: 61 model calls, 66 tool calls, played to 132 and to Game Over in Play mode, built and checked the exe |
| This version's changes in the published exe (a stand-in agent) | laptop | all as the changelog says; one empty line after a PowerShell error in the Terminal, fixed |

## Clean-up

What the panels added since 1.2.0 repeated, once now, and small fixes. No behaviour a user sees changed,
but the three fixes at the end.

- `BoxPicks`: File inspector, Image editor, Web preview and Terminal each put their picks in the box and
  listened for a card leaving it the same way, with a flag of their own; one class does it, and one
  20,000-character cap for what a pick tells the model (`BoxPicks.Capped`).
- `Debounce` and `FolderWatch`: the disk watchers (Code editor's tree, Web preview's page folder, Image
  editor's pictures) and the timers that read once a burst rests, and the Code editor's and Terminal's
  selection. Avalonia's `DispatcherTimer` constructor that takes a handler starts the timer at once: each of
  those panels read its folder or reloaded its page once more a quarter second after it opened. `Debounce`
  makes its timer stopped.
- `CodeView.SelectedLines`: the selection as lines, for the Code editor and the Terminal.
- Core: `FolderPath.IsIn` (four copies of "is this path in that folder"), `PastedFiles` (the stamped names
  of pasted pictures, marked areas and picked lines, and deleting those not sent), the Terminal's
  terminal.json written through `AtomicFile` (two commands ending at once shared one temporary file).
- `SidePanelNames`: the panels' names were written in the dropdown's list and again in Auto's rule; once
  now. Auto's reading of a call's arguments is shared.
- Gone: an unused brush helper, unused usings and a parameter, the Video editor's empty box handler; an
  SSH doc comment had lost its `<summary>`, two crefs didn't resolve.
- Fixed: the Terminal's Watch tooltip had File changes' words ("Follow the agent's changes"), it says
  "Show the newest command as it starts, scrolled to its end"; the Image editor's Make again tooltip still
  said to pick a made picture after the picked one was made; the Image editor's thumbnails are disposed.

`Directory.Build.props` says 1.3.0 and the changelog's section is dated. Published exe 28,491,177 bytes,
no trim warnings.

## Unity 2048 on the laptop

`unity2048-test.ps1 -Root .dev\release-qa\haiku -Unity <6000.3.10f1>`: the laptop has Unity 6000.3.10f1
(6.3), and Haiku through OpenRouter kept the Qwen server free for the workstation's runs.

It wrote the game (Game2048.cs, 9 KB) and a build script, played in Play mode with real key presses,
checking the board from screenshots move by move: slides, merges once per move, one new tile after a
move and none after a move that changes nothing, the score, past 128 (132), then R and long key runs to
Game Over (1364 points, 143 moves). It built Game2048.exe, played two moves and R in it, and closed it and
Unity. No bug to fix in the game; its three tool errors were Unity's -createProject answering 1 though the
project was made, and two look-ups. Checked after: the screenshots of Game Over and of the build's first
move, and the exe started again here.

## This version in the published exe

A stand-in agent (`.dev/release-check-1.3.0`: an OpenAI-style server whose model makes set calls, each
waiting for the driver) in the published exe, driven through UI Automation and posted mouse messages, at
125%. Its first reply streamed signed Claude thinking as LiteLLM gateways do (`thinking_blocks`).

| Step | What Auto showed, and what was checked |
|---|---|
| run_shell, 31 lines | Terminal: "Auto (Terminal)" in the dropdown, "Terminal" in the title bar over the panel; the command listed "This PC · exit 0 · 222 ms", all 31 lines; Watch on and greyed. Lines 3-5 dragged over went in as "Lines 3-5 · Write-Output 'Checking the project'; 1.…" with their file in Data\pasted; Esc took them out and deleted the file |
| read_file | stays on Terminal; the card says "Read notes.md", "Done in 27 ms" |
| write_file index.html | Web preview: the page, its console.error listed |
| write_file game.js beside it | the page reloaded by itself ("3 stars" from the script), "ReferenceError: missingFunction is not defined" at game.js:2:1. Error 2 clicked: "Error 2 · index.html"; Area and a drag: "Area · index.html" with its picture in the card, the error card gone; Esc twice: the picture deleted, Area off |
| write_file build.py, new in a git project | Code editor, build.py open (Python, UTF-8, LF) |
| edit_file README.md, there before | File changes: four files, README.md +1 -1 |
| write_file mockup.svg elsewhere | File inspector: SVG 320x200 |
| run_shell with Write-Error | Terminal: "exit 1" in red; the error starts with the command's own line, none of Sloperator's setup |
| the reply | the title bar: "Sloperator · Connected ● 16k of 256k used · Check the star site…" |

Every request after the first carried the thinking back whole, signature included. The × closed the
panel and kept Auto; the chat opened again from History brought the Terminal back with both commands
(terminal.json, no temporary file left). By hand: the Image editor's New picture ("New · Untitled
picture"), a picture copied into its folder as ComfyUI would (listed and shown in under a second, Make
again on with its tooltip), version 1 picked, a part marked ("Area of version 1 · Untitled picture", the
crop and the mask in Data\pasted) and sent: the model was told the redraw_area run with both files. The
Video editor opened by its new name. The dropdown lists None, Auto, File changes, File inspector, Code
editor, Image editor, Video editor, Web preview, Terminal. Settings → Advanced → Updates: "Version 1.3.0";
General: "Tool call approval". Nothing in errors.log.

Found and fixed: after a PowerShell error the Terminal listed an empty line (the error record ends in a
line break): "5 lines" for four. `ToolTests` checks it now.

## On the workstation

The workstation thread ran, on 0439af3 (Release build):

- Unit suites with `BB_DESKTOP_TESTS`, the SSH lab (a 3 GB copy, a reboot), real MCP servers through npx
  and `BB_COMFY_ONLINE`: App 284 of 285, Core 1220 of 1231 (7 skipped, run apart).
  `GatewayThinkingTests` on OpenRouter Haiku 5.5, `MunitoImageCapTests` and `TextOnlyServerTests` on
  munito gemma-4-12b: each 1 of 1.
- Not product bugs. That PC's code page is 1251: the ANSI code page test (fixed: it uses the PC's own),
  and two pause-key tests that fail on develop too. Two races between test classes: ComfyOnlineTests
  beside the Civitai stand-ins (fixed: one collection), ImageEditorTests deleting its folder while a
  thumbnail was read (fixed: waits, and retries).

## Left as they are

- With no context use known yet (a chat opened again, before its next request) the title bar reads
  "Connected ● · <chat>": the dot that stands between Connected and the use is followed by the chat's
  separator.
- The workstation's two pause-key tests on its code page or keyboard layout.
