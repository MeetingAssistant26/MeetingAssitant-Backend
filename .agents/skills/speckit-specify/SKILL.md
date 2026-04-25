---
name: "speckit-specify"
description: "Create or update the feature specification from a natural language feature description."
---

# Speckit Specify

Use this skill when the user wants to start a new Spec Kit feature or revise the requirements for an existing one.

## Canonical Workflow
- Read `.claude/commands/speckit.specify.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Prefer the helper scripts in `.specify/scripts/powershell/`, especially `create-new-feature.ps1`, when following the workflow.

## Compatibility
- Codex entrypoint: `$speckit-specify`
- Claude Code entrypoint: `/speckit.specify`
- Keep `.claude/commands/speckit.specify.md` as the source of truth for prompt behavior.
