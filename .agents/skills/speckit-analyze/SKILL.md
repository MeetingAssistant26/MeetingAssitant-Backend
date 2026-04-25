---
name: "speckit-analyze"
description: "Perform a non-destructive cross-artifact consistency and quality analysis for the current Spec Kit feature."
---

# Speckit Analyze

Use this skill when you need to review whether the current Spec Kit artifacts line up before implementation.

## Canonical Workflow
- Read `.claude/commands/speckit.analyze.md`.
- Treat the current user request as that command's `$ARGUMENTS` input.
- Execute the workflow directly in Codex using the repository files under `specs/` and `.specify/`.

## Compatibility
- Codex entrypoint: `$speckit-analyze`
- Claude Code entrypoint: `/speckit.analyze`
- Keep `.claude/commands/speckit.analyze.md` as the source of truth for prompt behavior.
