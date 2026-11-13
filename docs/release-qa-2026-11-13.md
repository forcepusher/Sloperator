# Release check, 13 November 2026

A pass before 1.5.0, branch `release/1.5.0` off develop 18c5e82d, on the laptop only: a clean-up of what
changed since 1.4.0 (7e9c2621: 289 commits, 198 without the merges), the unit suites, this version's changes
driven in the published exe with a stand-in model and with real models, and a Unity 2048 run as a sanity
check of the agent. Develop's two merges of the same morning (61f7fa94: Auto moves up at any time, never picks
below Medium and looks at every message; the blob past a finished plan's last point) were taken in at the end,
and the suites, the exe and a look at both were done again on that.

## In short

| Check | Where | Result |
|---|---|---|
| Unit suites | laptop | App 459 of 459, Core 1668 of 1668 (36 opt-in skipped), on the final branch |
| Published exe (`publish.ps1`) | laptop | 29,089,789 bytes, no trim warnings, 4 min 31 s |
| Unity 2048 (`unity2048-test.ps1`, Claude Haiku 5.5 through OpenRouter) | laptop | pass in 48 min 12 s on the third run, with a fix made here: the first two ended after 5 minutes on the server's content filter |
| This version's changes in the published exe (a stand-in model) | laptop | as the changelog says, but for one bug, fixed here (a chart kept its old bars after a sort) |
| Auto model and effort in the published exe (real, OpenRouter) | laptop | picked Claude Haiku 5.5 at Medium, a short "yes, thanks" looked at too |

## Clean-up

A build with the unused-member analyzers on (IDE0051, IDE0052) found nothing unused in the four projects. The
same few lines stood in several of the files written since 1.4.0, and each has one place now:

- The text width of a Word page from its section's page size and margins, three times (table of contents,
  header and footer, pictures): `WordPages.TextArea`.
- A column's letters as its number, seven times in the workbook code: `XlsxWriter.Column`.
- A relationship's target as a part name, twice with different edge cases: `PartSet.Resolve`.
- A delete to the Recycle Bin, three ways in the app: `RecycleBin.Put` everywhere.

No behaviour changes. `Directory.Build.props` says 1.5.0 and the changelog's section is dated 2026-11-13. Ten
merges since 1.4.0 changed code without a changelog line; each was a fix to something new in this version,
which a user never had, so none was added.

## Found and fixed

- **A reply the server's content filter stopped ended the task.** The first two Unity 2048 runs ended after
  5 min 24 s and 5 min 46 s on "Server error: Output blocked by content filtering policy", an error in the
  stream. Both times the game was written and working in Play mode, the score past 128, and Haiku was writing
  a batch of arrow keys to fill the board for the Game Over check (batches of 24 and 20 keys had gone through
  in the first run). The runs of earlier releases never met it. Now the model is told once that its reply was
  stopped and to give it another way, in shorter steps, and the request goes again, twice at most for a step
  (`OutputBlockedException`, `AgentSession.OutputBlockedNote`); a third stop ends the task as before. In the
  third run the filter stopped a reply 17 minutes in, the chat said so, Haiku wrote "my last reply was cut off
  by a content filter, so I'll keep the next moves shorter", and the run went on to its end.
- **A chart kept its old bars after its table was sorted.** In the exe, a bar chart made over four regions and
  then sorted by revenue showed North, South, East, West in the File inspector's preview beside a table that
  said South, North, West, East. A chart's part keeps the values of its cells; Excel reads the cells again on
  opening, the preview draws what is kept. Every edit of cells now reads each chart's cells again
  (`SheetChart.Refresh`). After the fix, in the exe: a sort and a changed cell in one edit, and the chart drew
  the four bars in the table's new order with the new number.
- **A timing test of the cache keep-warm failed once** in a full run (180 ms between two reads where it wanted
  200): how far apart they are goes by how long the first reply took. It asserts their order now; develop got
  the same fix the same morning, and its wording was kept in the merge.

## Unity 2048 on the laptop

`unity2048-test.ps1 -Root .dev\release-qa\haiku -Unity <6000.3.10f1>`, three runs.

| Run | Time | Model calls | Tool calls | End |
|---|---|---|---|---|
| 1 | 5 min 24 s | 34 | 51 | the server's content filter, at the Game Over check (score 1192, one cell left) |
| 2 | 5 min 46 s | 38 | 63 | the same, as the fill for Game Over began (score 128 reached, R checked) |
| 3, with the fix | 48 min 12 s | 137 | 436 | pass |

The third run wrote Board2048.cs, Game2048.cs and a setup and build script, played in Play mode with real key
presses (merges once per move, one new tile per move that changes the board, none otherwise, the score past
128), filled the board to Game Over at 1292, pressed R, built Game2048.exe, played a move in it and closed it
and Unity. Three tool errors, none that changed the game. It is three to six times as long as the runs of
1.4.0, for three reasons: a batch-mode Unity command of its own sat out its 10 minute limit; told to keep its
replies short after the filter's stop, Haiku pressed a few keys a
call for the rest of the run (107 press_keys calls, 118 screenshots), and from 15 minutes in two Sloperator
windows of another thread's tests on the same desktop took the focus again and again, so key presses were
lost and sent again. Its last click, on Unity's close button, landed on one of those windows; it noticed,
clicked nothing more there and said so in its answer. The user's own Unity project, open since the day before
with unsaved changes, was left alone all three times; the first two runs left their own Unity open, which was
closed by hand before the next.

## This version in the published exe

Rig `.dev/release-check-1.5.0/` (run1.ps1, run2.ps1, server.ps1, n.ps1, win.ps1, live-auto.ps1): the exe on a
scratch home, off the screen, with a stand-in whose model makes scripted tool calls.

First run, "Make the quarterly pack":

- **Plan bar**: five points (Workbook, Report, Deck, PDF, Check), the blob on the step the agent was on, and
  finished when it answered on the last step. Gone with the next message.
- **Excel**: a workbook with a bar chart under its table, then one edit that made the header bold on yellow,
  fitted the columns, froze row 1, sorted by revenue, highlighted the row over $46,000 in red and put filter
  buttons on the header. The total row was left out of the chart and the bars start at zero; the total showed
  $180,982.50 without Excel.
- **Word**: a report that got page numbers at the bottom, "Confidential" in the footer's left, a table of
  contents with pages (Summary 1, Regions 1, Outlook 2), its title centred in dark blue at size 20, and a
  comment on a sentence. read_file showed the contents, the footer and the comment.
- **PowerPoint**: a deck with a pie chart from a table, and "add slide after 2" with a title and two points:
  four slides, the new one third.
- **PDF**: `pages:` lines joined a one-page PDF and the Word report as it prints into a PDF of three pages,
  with the report's footer and page numbers on its pages.
- **Skip wait**: a wait of 60 s showed **Skip wait** and the Shift and Pause/Break keys on its card; pressed
  after 23 s, the agent was told "the user ended it early" and went on.
- **Stop** stood left of the blob while the task ran, Send where it always is.
- **Folded task**: "Worked for 46 s · 16 tool calls · qwen3-ui-test at Medium" with the tools as chips, the
  plan calls not counted; **Show steps** unfolded the cards and **Hide steps** stood in its place.
- **show_panel**: "File inspector" with the workbook opened it beside the chat.
- **Auto effort when the look at the task fails**: the stand-in answered the look with the task's own tool
  calls. Each was refused ("There is no tool write_file in this side task"), so nothing was written by it;
  after 12 replies the card said the evaluation failed and the task ran at Medium.
- **A long message**: 30 lines showed whole (a fold starts past 30); 60 lines showed 20 and **Show all 60 lines**.
- **Settings > Advanced**: "Version 1.5.0 · Up to date", the three Auto models, and **System prompt** with
  **View and edit**: the window showed the prompt with `{os}`, `{date}`, `{home folder}` and `{screens}` in
  yellow, "Default · about 4,135 tokens", Reset to default greyed.

Second run, with the fix, a sound and a picture in the home, and thirty models on the stand-in:

- **Auto effort**: the look answered "effort: high", the dropdown said "Auto (High)" and the card "at High".
- **edit_sound**: fade in over the first second saved "Fade in 0.00-1.00 s.wav" beside the sound. The **Audio
  editor** opened on it as version 2 of 2 with the faded waveform, its small copy in the list, the Edit line
  under it and Prompt metadata as a button.
- **Image editor**: opened on the newest picture, **Area** and **Brush** with the size slider, the version's
  small copy beside its number.
- **Model dropdown**: a search box over thirty models, **Auto** as the first row; typing "17" left the one
  model that has it.

Real models, OpenRouter, model and effort on Auto, before develop's Auto changes came in: "What is 17 times
23?" was answered 391 by Claude Haiku 5.5 at Low, and a question on ISO 8601 durations got the card
"anthropic/claude-haiku-5.5 at Low · It's a knowledge question answered in five lines with no files…" and a
good answer; $0.0038 for both, the cost from OpenRouter without "~".

On the final exe, with develop's two merges in:

- **Auto** on OpenRouter again: the same sum was answered by Claude Haiku 5.5 at Medium (Auto no longer picks
  below it), and "yes, thanks" after it got the look too and its own card, $0.0034 for both. The scratch home
  with the key was deleted after each run.
- **Plan bar**: the first run again; with every step done the blob stood past the last point, the point's dot
  in sight, and the chart of the sorted table drew South, North, West, East as the table has them.

The cache keep-warm showed by itself in the third Unity run ("Kept this chat in the server's prompt cache,
which lapses after 5 minutes…", once, during a batch-mode Unity command that ran into its 10 minute limit). Not run here: the Brush's strokes and a redraw
(they need ComfyUI and the real mouse), and Auto's move up to a dearer model with its **Switch** card. Their
unit tests pass.

## Left as they are

- The workstation's checks (its GPU, second monitor and Qwen server) weren't run: laptop only this time.
- Three Pause key tests failed on the workstation's develop earlier. No Core test failed on the laptop in four
  of five full runs (the fifth is the timing test above), so that is the workstation's to look at.
- After the filter's note the model keeps its replies short for the rest of the chat, which made the Unity
  run slow. A note that wears off after a few steps would be quicker; left for after the release.
- `unity2048-test.ps1` moves earlier runs aside before it starts, which fails without a word when the run
  before left Unity open on its project; the next run then starts beside the old one's files.
