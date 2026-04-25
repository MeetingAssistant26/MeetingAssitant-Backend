---
name: "speckit-implement"
description: "Execute the implementation plan by processing and completing the current feature tasks."
---

# Speckit Implement

Use this skill when the specification, plan, and task list are ready and the feature should be implemented.

## Canonical Workflow
- Read `.claude/commands/speckit.implement.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Use the current feature artifacts in `specs/` as the implementation source of truth.

## Compatibility
- Codex entrypoint: `$speckit-implement`
- Claude Code entrypoint: `/speckit.implement`
- Keep `.claude/commands/speckit.implement.md` as the source of truth for prompt behavior.
