---
name: "speckit-constitution"
description: "Create or update the project constitution and development guidelines for this repository."
---

# Speckit Constitution

Use this skill when the team wants to define or revise the governing principles in `.specify/memory/constitution.md`.

## Canonical Workflow
- Read `.claude/commands/speckit.constitution.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Update `.specify/memory/constitution.md` and any mirrored agent-context files the workflow calls for.

## Compatibility
- Codex entrypoint: `$speckit-constitution`
- Claude Code entrypoint: `/speckit.constitution`
- Keep `.claude/commands/speckit.constitution.md` as the source of truth for prompt behavior.
