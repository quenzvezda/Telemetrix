# OpenSpec in Codex

The skills under `skills/openspec-*/` adapt the existing Claude skills for Codex discovery. The workflow bodies in `../.claude/skills/` remain the source of truth.

## Load and follow the workflow

Read the full source skill linked from the selected Codex skill before taking workflow actions. Resolve links relative to the file containing them, even when the working directory is a subdirectory. If a required source file is missing or unreadable, report it and stop that workflow.

Follow the source workflow's steps, planning boundaries, scope, confirmations, completion checks, and handoffs. Read any references it requires when the relevant step is reached. A handoff remains a handoff; changing its invocation spelling does not authorize starting another workflow.

Apply the repository's `AGENTS.md` alongside the source workflow. In particular, its task-group review checkpoints bound the apply loop, and its manual review governs milestone completion. Read the current instructions rather than treating this adapter as a copy of them.

## Invocation

Treat a request's change name and other arguments as the source workflow's input. When presenting a workflow handoff in Codex, use the corresponding skill:

| Existing command | Codex skill |
| --- | --- |
| `/opsx:explore` | `$openspec-explore` |
| `/opsx:propose` | `$openspec-propose` |
| `/opsx:update` | `$openspec-update-change` |
| `/opsx:apply` | `$openspec-apply-change` |
| `/opsx:sync` | `$openspec-sync-specs` |
| `/opsx:archive` | `$openspec-archive-change` |

These are equivalent workflow requests, not registrations of Claude slash commands in Codex. For an inline workflow call (for example, archive calling sync), load the corresponding Codex skill and its source instructions, retaining the caller's selected paths, store, and scope.

## Tools and CLI

Use the available Codex tools for shell commands, file reads, searches, edits, and user questions, following the repository's lean-ctx instructions. Claude-specific tool metadata such as `allowed-tools: Bash(openspec:*)` is not a Codex tool registration or a grant of permissions. Preserve any workflow requirement to wait for a user answer when choosing the equivalent interaction tool.

Run the source's `openspec <arguments>` commands as `npx -y @fission-ai/openspec@1.14.3 <arguments>`, matching the version in `AGENTS.md` and the existing generated skills. Preserve arguments, quoting, JSON output, and selected `--store` flags, including in follow-up commands printed by the CLI. Run from the repository unless the source explicitly selects another root or store. A shell command's output is evidence only according to the repository's raw-output rules.

Keep the existing `.claude/commands/`, `.claude/skills/`, OpenSpec configuration, and milestone artifacts intact when maintaining this adapter. Workflow changes belong in the original workflow source; Codex compatibility changes belong here.
