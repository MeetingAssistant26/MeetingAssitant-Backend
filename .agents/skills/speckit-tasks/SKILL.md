---
name: "speckit-tasks"
description: "Generate an actionable, dependency-ordered tasks document for the active Spec Kit feature."
---

# Speckit Tasks

Use this skill when the feature plan is ready and it is time to break implementation into sequenced tasks.

## Canonical Workflow
- Read `.claude/commands/speckit.tasks.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Generate or refine the feature task list in the active `specs/<feature>/tasks.md`.

## Compatibility
- Codex entrypoint: `$speckit-tasks`
- Claude Code entrypoint: `/speckit.tasks`
- Keep `.claude/commands/speckit.tasks.md` as the source of truth for prompt behavior.
