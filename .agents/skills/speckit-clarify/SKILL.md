---
name: "speckit-clarify"
description: "Identify underspecified areas in the current feature spec and resolve them with targeted clarification."
---

# Speckit Clarify

Use this skill when the current feature spec has ambiguity, missing acceptance details, or open requirement questions.

## Canonical Workflow
- Read `.claude/commands/speckit.clarify.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Apply the clarification flow against the active feature files under `specs/`.

## Compatibility
- Codex entrypoint: `$speckit-clarify`
- Claude Code entrypoint: `/speckit.clarify`
- Keep `.claude/commands/speckit.clarify.md` as the source of truth for prompt behavior.
