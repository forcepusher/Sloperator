# Release check, 3 November 2026

A pass before 1.4.0, branch `release/1.4.0` off develop 3733396, on the laptop only: a clean-up of what
changed since 1.3.0 (b790a7d), the unit suites, this version's changes driven in the published exe with
a stand-in model, and a Unity 2048 run as a sanity check of the agent.

## In short

| Check | Where | Result |
|---|---|---|
| Unit suites | laptop | App 312 of 312, Core 1277 of 1277 (27 opt-in skipped), after the clean-up |
| Published exe (`publish.ps1`) | laptop | 28,629,398 bytes, no trim warnings, 3 min 40 s |
| Unity 2048 (`unity2048-test.ps1`, Claude Haiku 5.5 through OpenRouter) | laptop | pass in 14 min 8 s: 63 model calls, 103 tool calls, score past 128 in Play mode, then Game Over, built and played the exe |
| This version's changes in the published exe (a stand-in model) | laptop | all as the changelog says |

## Clean-up

- A side task's tool call went through its own copy of the argument handling: no JSON repair, no name
  hints, and wrong arguments came back without the tool's parameters. It now shares the main
  conversation's (`AgentSession.PrepareCall`, `ArgumentProblem`).
- The chat card read a side_task call's prompt with a JSON parse of its own beside
  `SideTaskTool.OutputIn`; both read through one helper now (`SideTaskTool.PromptIn`).
- A stray blank line in `MainWindowViewModel`.

`Directory.Build.props` says 1.4.0 and the changelog's section is dated 2026-11-03.

## Unity 2048 on the laptop

`unity2048-test.ps1 -Root .dev\release-qa\haiku -Unity <6000.3.10f1>`. It wrote Game2048.cs and a build
script, played in Play mode with real key presses (merges once per move, one new tile per move that
changes the board, none otherwise, the score past 128, later 1392), filled the board to Game Over, pressed
R, built 2048.exe and played a move in it, and closed Unity. It used plain `wait` 32 times while Unity
worked. The haiku root's old `Data` folder was renamed to `SloperatorData` by the CLI's first start, with
its settings kept.

## This version in the published exe

Rig `.dev/release-check-1.4.0/` (run.ps1, server.ps1): a home with its settings in an old `Data` folder,
and a stand-in whose model waits a second, waits for a change, hands "Count the notes" to a side task with
output on (it reads a file and answers "3 notes."), then answers at length. Every reply reports 2000
prompt tokens (1500 cached) and 100 out; side task requests get their own plan.

- `SloperatorData`: the `Data` folder was renamed on the first start, and the settings in it were used.
- Waits: `wait` came back without a screenshot (none in the next request), `wait_until_change` with one;
  their cards say "Wait 1s" and "Wait up to 2s for the screen to change".
- Auto pricing lookup: the first reply had no cost, so right after its call a side task "Look up the prices
  of qwen3-release-test" ran (one request, its set_api_pricing call ended it), with an Input foldout and no
  Output. The prices were kept in settings.json (3 / 0.3 / 3.75 / 15 $), and the main conversation holds
  no lookup call.
- side_task: "Count the notes" ran its read_file in its own conversation; the card has Input and Output,
  and "3 notes." went back to the chat.
- The top bar said "~$0.0242": 7 replies (4 of the chat, 3 of the side tasks) at 3.45 thousandths of a
  dollar each by those prices. Cost estimates showed the same, the tokens (3.5k in, 10.5k from the cache,
  700 out), the cache saving (~$0.0284), the prices as kept, and the chat in All chats.
- A server that reports cost (run with 0.0123 per reply): "$0.0246" without "~", and no lookup.
- Side panel dropdown: 231 px wide under Auto, Cost estimates and None.
- Chat selection at the chat's end: selecting the last line's end brought the card over the box, and the
  chat moved 28 px, just enough to keep the selection's end in view, instead of following the end; after
  a click away it was back at its end. A selection two lines higher didn't move the chat at all.

## Left as they are

- The workstation's checks (its GPU, second monitor and Qwen server) weren't run: laptop only this time.
