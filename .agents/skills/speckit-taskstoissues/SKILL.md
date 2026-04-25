---
name: "speckit-taskstoissues"
description: "Convert existing Spec Kit tasks into actionable, dependency-aware GitHub issues."
---

# Speckit Tasks To Issues

Use this skill when a generated task list should be translated into GitHub issues for tracking.

## Canonical Workflow
- Read `.claude/commands/speckit.taskstoissues.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- If GitHub issue creation is unavailable in the current environment, report the blocker clearly instead of guessing.

## Compatibility
- Codex entrypoint: `$speckit-taskstoissues`
- Claude Code entrypoint: `/speckit.taskstoissues`
- Keep `.claude/commands/speckit.taskstoissues.md` as the source of truth for prompt behavior.
