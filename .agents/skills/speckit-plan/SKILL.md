---
name: "speckit-plan"
description: "Execute the implementation planning workflow and generate the technical design artifacts for the active feature."
---

# Speckit Plan

Use this skill when a feature spec exists and the next step is to turn it into a technical implementation plan.

## Canonical Workflow
- Read `.claude/commands/speckit.plan.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Prefer the helper scripts in `.specify/scripts/powershell/`, especially `setup-plan.ps1`, when following the workflow.

## Compatibility
- Codex entrypoint: `$speckit-plan`
- Claude Code entrypoint: `/speckit.plan`
- Keep `.claude/commands/speckit.plan.md` as the source of truth for prompt behavior.
