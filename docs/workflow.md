# Workflow (orchestrator rules)

The main session is the **orchestrator** (Opus, low effort). It plans, splits work, delegates, reviews summaries and diffs, and decides what merges. It doesn't write large amounts of code itself. Keep its context small: read summaries and diffs, not whole files.

## Subagents (`.claude/agents/`)
| Agent | Model / effort | Use for |
|---|---|---|
| `scout` | sonnet, low, read-only | Recon, grepping, decompiling, drafting and filing issues, mechanical docs |
| `implementer` | sonnet, medium, own git worktree | One narrow code task, which becomes one PR |
| `reviewer` | sonnet, medium, read-only, fresh context | Reviewing a PR against its acceptance criteria |

- Escalate to opus (medium) only after a sonnet agent has failed the same task twice. Record why in the issue.
- If the agent types haven't registered, use `general-purpose` with `model: sonnet` and paste the agent file's rules into the brief.

## Briefs
Every brief contains:
- one narrow task;
- the files and dirs the agent may touch;
- acceptance criteria;
- how to verify;
- the return contract: a summary of 200 words or fewer plus a PR/issue link, never whole files.

Further rules:
- Two agents never write the same files at the same time.
- Never resend a failed brief unchanged: narrow it, add the failure output, or escalate.
- Remind agents of the rules in `CLAUDE.md` (fork only, no DLLs, no token, verified APIs).

## Git, PRs and issues
- One branch and one PR per logical change, aiming for under about 400 changed lines. Conventional-commit titles.
- PR body sections: What / Why / How verified (real command output) / TaleWorlds APIs used (assembly + type + member) / Risks / Follow-ups.
- Every PR gets a `reviewer` pass. Merge (squash) only when the build and tests are green and blocking comments are fixed.
- File an issue for every bug, game-update incompatibility or out-of-scope finding, instead of fixing it inline.
  - Labels: `bug`, `game-update`, `harness`, `tech-debt`, `question`.
  - Include repro steps and file:line.
- Keep pinned tracking issue #2 up to date as work lands.
- Agent worktrees live in `.claude/worktrees/` (git-ignored). Remove them after their PR merges.

## Stop and ask the owner when
- a phase decision is ready;
- anything needs Steam login or other credentials (including a new server token);
- licensing or redistribution questions come up (for example, committing decompiled code);
- a design choice blocks progress, or the same task has failed 3 times.
