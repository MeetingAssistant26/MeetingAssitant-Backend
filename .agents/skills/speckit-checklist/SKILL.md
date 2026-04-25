---
name: "speckit-checklist"
description: "Generate a custom checklist for the current Spec Kit feature based on user-provided quality criteria."
---

# Speckit Checklist

Use this skill when the user wants a quality checklist for a feature, specification, plan, or implementation review.

## Canonical Workflow
- Read `.claude/commands/speckit.checklist.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Build or update checklist artifacts in the current feature directory under `specs/`.

## Compatibility
- Codex entrypoint: `$speckit-checklist`
- Claude Code entrypoint: `/speckit.checklist`
- Keep `.claude/commands/speckit.checklist.md` as the source of truth for prompt behavior.
