# Release check, 17 November 2026

A pass before 1.5.1, branch `release/1.5.1` off develop 8cd357ca, on the laptop only. Since 1.5.0 there are two
changes a user sees, both in the chat: Auto's card says what it did and its reason in full (design A), and the
blob sits left of Stop and Send. The pass: a clean-up, the unit suites, both changes driven in the published exe
with a stand-in model, and a Unity 2048 run as a sanity check of the agent.

## In short

| Check | Where | Result |
|---|---|---|
| Unit suites | laptop | App 467 of 467, Core 1669 of 1669 (36 opt-in skipped) |
| Published exe (`publish.ps1`) | laptop | 29,092,861 bytes, no trim warnings, says 1.5.1 (one NuGet advisory, see below) |
| This version's changes in the published exe (a stand-in model) | laptop | as the changelog says |
| Unity 2048 (`unity2048-test.ps1`, Claude Haiku 5.5 through Anthropic's API) | laptop | pass in 14 min 11 s, once its question about installing Unity's CLI package was answered no |

## Clean-up

The code since 1.5.0 was tidied when it merged (d3e7df6d), and the same build with the unused-code analyzers on
(IDE0051, IDE0052, IDE0060, IDE0005) found no unused member anywhere. It found seven `using` lines nothing used,
in five files of the app and Core and two tests, and with the XML documentation built, a stray fragment
("|---| line followed it).") left after a `</param>` in `MarkdownDocument.cs` and a `cref` to
`SheetFormula.WriteValues` that two overloads matched. All fixed in 84e6158a; no behaviour changes.
`Directory.Build.props` says 1.5.1 and the changelog's section is dated 2026-11-17.

The publish prints NU1903 for `Microsoft.DotNet.ILCompiler` 10.0.13 (GHSA-p3jp-9v83-76w9), from
`scripts/pack-runtime.cs`, the build-time tool that packs the native libraries. It was in 1.5.0's publish logs
too; the package comes with the laptop's .NET SDK and isn't in the exe. Not changed here.

## This version's changes in the published exe

Driven off screen with `.dev/auto-card-test/run.ps1` against its stand-in server (Auto on, three Claude names so
the helper has its tiers), shots in `.dev/auto-card-test/rel-1.5.1/`:

- **Picked.** Folded, the run shows "Worked for 8.6 s · 1 tool call · claude-sonnet-5-5 at Medium" and the answer.
  Show steps opens Auto's card first under that line: "Picked claude-sonnet-5-5 at Medium for this message",
  "Picked in 7.2 s", the helper's two-line reason whole and wrapped under it, then the open foldout "Notes it
  passed to claude-sonnet-5-5" with the notes alone.
- **Wants a dearer model.** "Wants claude-opus-5-5 at High for this message", "Waiting for your answer", the reason
  under it, and the Switch card over the message box. After **Stay**: "Stayed on claude-sonnet-5-5 at Medium for
  this message, not claude-opus-5-5", folded under its own "Worked for" line.
- **The pick failed** (the helper's server answers 500): "Kept claude-sonnet-5-5 at Medium for this message: the
  pick failed", "Failed after 30 s", and the run went on at Medium.
- **The blob.** While a run goes on, the bar reads blob, **Stop**, **Send** from the left; idle, the blob sits
  beside **Send**, which stays where it is.

## Unity 2048

Claude Haiku 5.5 through Anthropic's API (the `release-qa\anthropic-night` root), Unity 6000.3.10f1, with `-Then` answering the setup question as in 1.5.0's third pass. After 30 s Haiku found Unity's own CLI and asked whether to install its Pipeline package or go on with Unity.exe in batch mode; told no, it built the game in one script (`Game2048.cs`, 11,070 bytes), play-tested it in the Editor's Play mode with real key presses, made the Windows build and checked it (two tiles at the start, a Left and Down move merged 2+2 into a 4 and set the score to 4), then closed the build and Unity. It said it didn't press R in the build. 88 model calls, 184 tool calls, 2 tool errors, no resets; the prompt cache held (258.6k of the last 261.4k prompt tokens read from it).
