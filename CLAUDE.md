# Persistent Empires: agent guide

Persistent Empires is a Bannerlord multiplayer mod. We are porting it to the current game (v1.4.8) and building a headless test harness so an AI agent can run change → build → test → pass/fail in seconds.

## Start every session here
1. Read `.claude/current-task.md`. It holds the one task in progress (git-ignored, local only). If it's missing, nothing is in flight; take the next unchecked item from tracking issue #2.
2. Check status: `gh issue view 2` (the pinned checklist of phases and PRs) and `gh pr list`.
3. Read the doc for whatever you're touching (see the map below). Don't read them all up front.

## Non-negotiable rules
- **Fork only.** All git, PR and issue work goes to `adoophus/PersistentEmpires-OpenSourced` (`gh` defaults to it). Never push, comment, open issues or link to AR-Development upstream without asking the owner. Write upstream refs as a code span, e.g. `upstream #57`, never as a link.
- **Never commit TaleWorlds DLLs** or large decompiled TaleWorlds source. Reference the game through `$(BannerlordClientPath)` / `$(BannerlordServerPath)`.
- **Never print, read into context, or commit the server auth token** (see `docs/environment.md`).
- **Never invent TaleWorlds API names.** Confirm each one with `ilspycmd` against the installed DLLs, and cite it as `Assembly.dll!Type.Member` in PRs.

## Doc map
| Doc | Read when |
|---|---|
| `docs/environment.md` | Installs, paths, the token, booting the dedicated server, tools |
| `docs/workflow.md` | Orchestrating: agent roles and cost rules, briefs, the PR/issue process, when to stop and ask the owner |
| `docs/roadmap.md` | Picking the next task: phase deliverables and definition of done |
| `docs/headless-feasibility.md` | Why the harness is designed the way it is (tier decision, engine evidence) |
| `.claude/agents/*.md` | Subagent definitions (scout, implementer, reviewer) |

## Build / test loop
```powershell
scripts/setup.ps1 -ServerPath D:\bannerlord-headless\server   # writes Directory.Build.props.user (about 0.3 s)
scripts/build.ps1                                              # exit 0 ok, 1 build failure (about 3 s)
```
`scripts/server.ps1`, `scripts/test.ps1` and `PE.TestHost` don't exist yet (see `docs/roadmap.md`). Update this section as each one lands.

## Keeping docs current
- When a task starts, **overwrite** `.claude/current-task.md` with it. Don't append, and remove whatever is no longer relevant. When the task is done, delete the file or replace it with the next task.
- Durable knowledge goes in the matching `docs/*.md` file, via a PR. Status goes in issue #2. Neither belongs in `current-task.md`.
