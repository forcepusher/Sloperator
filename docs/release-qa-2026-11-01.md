# Release check, 1 November 2026

A last pass before 1.3.0, branch `release/1.3.0` off develop 36ae5f8 (and again for develop e4abe7f and a863246, see the second and third passes): a clean-up of what changed since
1.2.0, the unit suites, the new panels and the rest of this version's changes driven in the published exe
on the laptop, a Unity 2048 run as a sanity check of the agent, and on the workstation what needs its
programs, its GPU, its second monitor or the Qwen server.

## In short

| Check | Where | Result |
|---|---|---|
| Unit suites | laptop | App 285 of 285, Core 1220 of 1220 (20 opt-in skipped), after the clean-up and again after the fixes |
| Unit suites with the opt-in tests on | workstation | App 285 of 285, Core 1223 of 1232; the 2 failures fail on develop there too (pause keys on its code page) |
| ComfyUI tasks (`comfy-test.ps1`, Qwen) | workstation | every output good; video timed out after a usable clip, as at 1.2.0 |
| Second-monitor tasks | workstation | Haiku 5.5 4 of 4, Qwen 4 of 4 (mm-move timed out at 1.2.0) |
| Unity 2048 on Qwen | workstation | pass in 2 h 12 min: 192 model calls, played to 4588, built and checked the exe |
| Unity 2048 (`unity2048-test.ps1`, Claude Haiku 5.5 through OpenRouter) | laptop | pass in 12 min 51 s: 61 model calls, 66 tool calls, played to 132 and to Game Over in Play mode, built and checked the exe |
| This version's changes in the published exe (a stand-in agent) | laptop | all as the changelog says; one empty line after a PowerShell error in the Terminal, fixed |
| ssh, the Terminal and the release exe on the lab VM | workstation | setup lines in the Terminal on the first ssh command, fixed; the rest as the changelog says |
| Second pass: the day's later changes (hands-off, caching, capture, chat selection, key steps) | laptop | App 303 of 303, Core 1241 of 1241; Auto stuck on the Terminal after a command and a file in one go, fixed; Unity 2048 on Haiku passed again (14 min 6 s) |
| Second pass on two monitors, ShadowPlay and Qwen | workstation | suites with the opt-in tests on: App 303 of 303, Core 1248 of 1264 (16 need a key); second-monitor tasks Qwen 4 of 4, Haiku 4 of 4; Instant Replay recorded throughout; hands-off and Pause/Break as meant |
| Third pass: the evening's merges (document captures, Preview anyway, max_tokens always, duplicate keys) | laptop | App 307 of 307, Core 1255 of 1255; every change as its changelog line says; a real Haiku Word before/after in 24 s; Unity 2048 on Haiku passed (12 min 20 s) |

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
editor, Image editor, Video editor, Web preview, Terminal. Settings → Advanced → Updates: "Version 1.3.0"
beside Check for updates, then "Version 1.3.0 · Checking…" and "Version 1.3.0 · Up to date" against GitHub
(feature/version-status, merged into the release branch on the user's word; the exe published again);
General: "Tool call approval". Nothing in errors.log.

Found and fixed: after a PowerShell error the Terminal listed an empty line (the error record ends in a
line break): "5 lines" for four. `ToolTests` checks it now.

## On the workstation

The workstation thread's runs (two monitors, RTX 5070 Ti, the SSH lab VM, Qwen 3.8 27B q6_k_xl on the
user's server), Release builds of the branch.

| Test | Result |
|---|---|
| Unit suites with `BB_DESKTOP_TESTS`, the SSH lab (a 3 GB copy each way, a reboot), real MCP servers through npx and `BB_COMFY_ONLINE`, on 4f6b241 | App 285 of 285, Core 1223 of 1232 (7 skipped, run apart); the 2 failures are pause-key tests that fail on develop there too |
| `GatewayThinkingTests` (OpenRouter Haiku 5.5), `MunitoImageCapTests`, `TextOnlyServerTests` (munito gemma-4-12b) | 1 of 1 each |
| `comfy-test.ps1` on Qwen | every output good, 7 of 9 by the script (below) |
| Second-monitor tasks (`agent-tasks.ps1` mm-*) | Haiku 5.5 through OpenRouter 4 of 4 (12-38 s, 0 tool errors), Qwen 4 of 4 (36-79 s, 0 tool errors); also proxyapi.ru Haiku 3 of 3 and OpenRouter qwen3.8-27b 4 of 4 |
| The release exe, Haiku 5.5 through OpenRouter, on the lab VM (Windows 10 LTSC, WebView2 156) | as the changelog says, after the ssh fix (below) |
| The Image editor on real ComfyUI | 4 versions made with DreamShaper 8, a part redrawn as version 5 in 17 s |
| Unity 2048 on Qwen (`unity2048-test.ps1`, Unity 6000.3.23f1, on e18edf7) | pass in 2 h 12 min 32 s: 192 model calls, 198 tool calls, 5 tool errors, 7 context resets (each with its note), 0 reconnects |

- comfy-test: picture 2 min 51 s, img2img 6 min 15 s, named 1 min 53 s, sound 2 min 12 s, music
  2 min 41 s, speech 2 min 55 s, a 3D model 17 min 33 s (a .glb, 3 tool errors on the way), the combo
  7 min 59 s; video ran its 45 minutes out as at 1.2.0 (a usable 3 s clip first, then retakes). The two
  the script counts as failed are its own checks: the speech file was named good-morning-voice.mp3
  (4.0 s) with no folder for the checker, and the CLI's log of the combo's comfyui result stopped after its
  first file, though the second has its sound (3.01 s by ffprobe).
- mm-move, which Qwen timed out on at 1.2.0, passed.
- Unity 2048 on Qwen: the game made in Unity 6.3 and played in Play mode with real arrow keys, the score
  going up by each merge exactly, to 136 in a game it checked move by move and 4588 in a later one; Game
  Over on a full board and R checked; Unity2048.exe built, played, R pressed there too. It fixed four
  things of its own on the way, among them com.unity.ugui missing from manifest.json and KeyCode.Left and
  the like where Unity wants LeftArrow. A first attempt doesn't count: the Qwen server dropped after 1 h 5
  min, and Sloperator retried with its backoff until the script's limit, as it should.
- The release exe: Auto showed the Terminal, then File changes, then the Web preview; the window widened
  once and never resized between panels; the title bar had the status, the context used and the chat's
  title, the panel's name over the panel. Terminal rows "admin@127.0.0.2 · exit 0 · 1.1 s" and "This PC",
  lines dragged into the box, terminal.json written; the Web preview reloaded on an edit from outside,
  listed console.error, and an area went in ("Red."); History brought back 5 commands and the last page.
  Nothing in errors.log.
- Found and fixed there (88d509b, from its branch fix/ssh-terminal-setup-lines): the first ssh command
  after logging in (and after a reboot) showed four more lines in the Terminal panel and terminal.json
  than it printed: the terminal's echo of Sloperator's setup, the prompt, readline's line, sometimes "Last
  login". The model's copy was clean. The ssh tool handed the panel the command before `RunAsync` started
  the terminal; `RunAsync` now holds it back until the terminal is up. A new opt-in SSH test fails without
  the fix; the Ssh and CommandLog tests pass 84 of 84 against the lab.
- On 0439af3, three more failed, none a product bug, all fixed in 61c6e2c: the ANSI code page test
  (that PC's code page is 1251; it uses the PC's own now), ComfyOnlineTests beside the Civitai stand-ins
  (one collection now), ImageEditorTests deleting its folder while a thumbnail was read (waits and retries).

## Second pass: what came after

1.3.0 wasn't tagged when seven more merges reached develop the same day (hands-off: the agent waits for the
user to leave the mouse and keyboard, locks them for each action and shows a yellow frame; prompt caching
on OpenAI-format servers; screenshots through the magnifier, so ShadowPlay keeps recording; selected chat
text as context; timed key steps; max_tokens after a length cut; the window back on top after a click
through it; the Web preview's WebView2 notice). The user wanted them in 1.3.0, so develop e4abe7f was
merged into the release branch and the pass made again for them.

- The new code was already tidy: the analyzers found one redundant cast. Their changelog lines went into
  the 1.3.0 section; "No more COMException when Auto swaps the Web preview" went, as no released version
  had Auto or the Web preview.
- Unit suites on the merge: App 303 of 303, Core 1241 of 1241, twice, and again on the fix below. PictureFolderTests' mask test failed
  once while the exe was being published beside the suite (seen once before at 1.3.0's first pass), and
  passed alone and in the two full runs after.
- Found and fixed: Auto opened the panel of a written file by the chat's newest change since the last call.
  A command and then a file in one go (two calls of one reply), with the window's thread still opening the
  Terminal when both had ended, gave the file to the command, and Auto stayed on the Terminal where the
  Code editor was due. It goes by the call's own path now. Reproduced with a stand-in agent (a page, a
  command, a Rust file, no waits between them) and traced in a Debug build; after the fix the same run
  ends on the Code editor. It was in the first pass's build too.

Checked in the app, with a stand-in agent that waits for nothing between calls:

| Change | What was seen |
|---|---|
| A reply cut at the server's length limit (finish_reason "length", the usage reported) | "The server stopped the reply at its length limit; asking the model to go on. From now on it is asked for replies of up to 64k tokens."; every request after it had max_tokens 64000. Without usage in the reply it is taken for the context running out, as before |
| Screenshots | the picture the model got has the desktop where Sloperator's window was |
| Hands-off | a press_keys and a mouse_move while a script nudged the mouse a pixel every 300 ms (SendInput, untagged): the move waited until a second after the nudges stopped (6.4 s); the yellow frame round the screen in a capture meanwhile, and not in the agent's own screenshots |
| Chat selection | part of the reply dragged over: "Selected in the chat" over the box, and the model told "[The user selected this in the chat, which is what they refer to: …]" |
| Key steps | a series of only "wait 0.3" pressed no key, its card "Keys: wait 0.3" (the live Notepad check was its own thread's) |
| Rust | main.rs opened in the Code editor as Rust |
| The Web preview swapped out as WebView2 starts | no notice in the chat, nothing in crash.log or errors.log |

Unity 2048 on the laptop again (Haiku 5.5 through OpenRouter), as hands-off changes how every action is
made: passed in 14 min 6 s, 69 model calls, 112 tool calls, 3 tool errors (its own setup, Unity exiting 1
on a first scene and build run), no action held back. Played to 148 in Play mode, a full board to Game
Over (2424), R, and the built Unity2048.exe checked. The published exe after the fix (28,513,846 bytes, no
trim warnings) ran the same stand-in steps to the Code editor, nothing in errors.log.

The workstation's part of this pass (two monitors, ShadowPlay during a run, the second-monitor tasks with
hands-off, the release exe on Qwen) is below.

### On the workstation

Release builds of 727ad37 (the merge), then of 0a9aec7 (the Auto fix) for the runs in the published exe;
two monitors (2560x1080 primary, 1920x1080 to its right), Qwen 3.8 27B q6_k_xl on the user's server and
Claude Haiku 5.5 through OpenRouter. The frame and the lock were watched from outside: a script read the
screens' edges with BitBlt + CAPTUREBLT every 200 ms for the frame's colour, and moved the mouse a pixel
with SendInput, untagged as a user's input comes, to see whether it got through.

| Test | Result |
|---|---|
| Unit suites with `BB_DESKTOP_TESTS`, the SSH lab (a 3 GB copy each way, a reboot), real MCP servers through npx and `BB_COMFY_ONLINE`, on 727ad37 | App 303 of 303, Core 1248 of 1264 (16 skipped: they need a server key); the two pause-key tests that failed there before passed |
| The lock and the agent's clicks on each screen (`UserInputTests`, `ClickLandsTests`, pointed at screen 2 by a local edit, then at screen 1) | 3 of 3 on each |
| Second-monitor tasks (`agent-tasks.ps1` mm-*, the CLI built Release) | Qwen 4 of 4 (88-237 s, 1 tool error), Haiku 5.5 4 of 4 (15-43 s, 0 tool errors) |
| ShadowPlay with Instant Replay on, Haiku 5.5 in the published exe (0a9aec7) | Instant Replay recorded throughout; Sloperator's window not in the agent's screenshots |
| A real Qwen task in the published exe, 727ad37 and 0a9aec7 | the frame from the first click to the end; the agent waited while the mouse moved; Pause/Break gave the input back at once |
| App suite on 0a9aec7 | 303 of 303 |

- The frame in the second-monitor tasks: on screen 2, where the window to work in was, from the first
  click or key press (screenshots, zooms and commands before it put none up) until the run ended, and gone
  after. In mm-move it went up on screen 1 with the first drag and went over to screen 2 with the first
  action after a screenshot of screen 2. The agent waited where the mouse had moved less than a second
  before ("Waiting for you to leave the mouse and keyboard" in the CLI's log), and a nudge sent during one
  of Qwen's clicks didn't move the mouse.
- ShadowPlay: CaptureCore.log had Instant Replay started a few minutes before the run (Telegram had
  stopped it earlier in the day with its protected content; it was closed for this) and no DVR stop or
  protected-content line from then until after the run. The task, a sum on the Calculator's buttons typed
  into Notepad, took 42 s with a screenshot after each click. A capture from outside had Sloperator's
  window over Notepad; the agent's screenshot of the same screen shows Notepad and the desktop there.
- The real Qwen task (the same sum, 6912, and "6912 is the sum" in Notepad, right both times): no frame
  during the first screenshot and capture, the frame on screen 1 from the first click, gone when the
  reply came. On 0a9aec7 the mouse was moved from outside every 150 ms from the third second after the
  frame: the next click's card read "Waiting for you to leave the mouse and keyboard · 0:00", still at
  0:04 when the moves stopped, and it clicked 1.7 s later. No move was held back while the agent was
  thinking.
- Pause/Break during long typing (the numbers 1 to 800 asked for in one type_text): a nudge then went
  nowhere; Pause/Break pressed, a nudge 31 ms later moved the mouse; the card "Stopped after 622 ms", the
  chat "Stopped with Pause/Break.", the frame gone. Notepad ended with 400 characters, two of type_text's
  200-character pieces: it stops between pieces, so the piece under way when the key came is finished
  (about 0.2 s of typing at most; Notepad showed 178 characters at the key, behind what had been sent).

## Third pass: the merges after the second

Still untagged, 1.3.0 took six more merges of the evening (develop a863246), on the laptop alone as the
user asked: capture_result's pictures (one under the final reply for each thing shown, part of a window,
the after taken again by compare alone), pictures of document pages (Office or LibreOffice turning Word,
Excel and PowerPoint into a PDF out of sight), the File inspector's Preview anyway for files downloaded
from the internet and its document pages without a Windows previewer (Get LibreOffice), Word table cells
edited by their rows, tool calls that give a key twice, and max_tokens asked on every request.

- The new code: the analyzers found one redundant cast, in a collection expression where taking it out
  isn't worth the risk; left. Three merges had no changelog line: capture_result's pictures went into its
  1.3.0 line, the duplicate-key crash and Preview anyway got Fixed lines. The max_tokens line took the place
  of the second pass's length-cut line, which it supersedes.
- Unit suites on the merge: App 307 of 307, Core 1255 of 1255.

Checked in the app with a stand-in agent, and once with a real model:

| Change | What was seen |
|---|---|
| max_tokens | 64000 on every request, the first too |
| A Word page before and after | capture_result with the .docx and page 1 took "page 1 of 1 of sales.docx as capture 1" (Word ran hidden; nothing opened on screen); edit_file changed the table's cell by its row ("Replaced the text; the formatting around it is as it was"); capture_result with compare alone took the same page again and put the two together, "Before" over 140 and "After" over 150 |
| Pictures under the reply | only the comparison, captioned, under the final reply: no separate before |
| A key given twice | write_file with `"append": "line two\n", "append": true` appended the text (the file read "line one", "line two"), where it ended the run before |
| A workbook marked as downloaded (Zone.Identifier, zone 3) | the inspector: "2 KB, its text only (Windows doesn't preview files downloaded from the internet)" and **Preview anyway**, which showed the sheet; the file kept its mark |
| A real task, Claude Haiku 5.5 through OpenRouter, CLI | "change South's sales from 95 to 120, and show me the table before and after": the page captured, the cell edited by its row, the after compared, in 24 s, 4 model calls, 0 tool errors; the picture right |

Not checked here: document pages through LibreOffice and Get LibreOffice's 360 MB download (the laptop has
Office, which the inspector and the captures use first); their unit tests cover the paths.

Unity 2048 on Haiku once more, the prompt's capture rules having changed: passed in 12 min 20 s, 52 model
calls, 75 tool calls, 1 tool error, played to exactly 128, Game Over, R, the build checked. It showed no
picture of the game at the end (no capture_result call); the command-line run's check doesn't ask for one,
but the new-work rule would have it show the game. The published exe (28,548,807 bytes, no trim warnings) ran
the stand-in steps through to the comparison under the reply, nothing in errors.log.

## Left as they are

- With no context use known yet (a chat opened again, before its next request) the title bar reads
  "Connected ● · <chat>": the dot that stands between Connected and the use is followed by the chat's
  separator.
- A redrawn part's surroundings are not pixel-exact: the picture goes through the model's VAE, a mean
  change of 2 of 255 outside the mask. Pasting the original back outside the mask (ImageCompositeMasked in
  sloperator/redraw_area) would keep them exact.
- Haiku through OpenRouter once sent capture_result's arguments with their names run together
  ("captionNotepad"): they were left out without a word, and the whole screen was captured, uncaptioned.
- The workstation's two pause-key tests on its code page or keyboard layout.
