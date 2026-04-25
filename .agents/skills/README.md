# Spec Kit Skills for Codex

This repo keeps the existing Claude Code Spec Kit commands in `.claude/commands/` and adds matching Codex skills in `.agents/skills/`.

## Skill Mapping
- `$speckit-constitution` -> `.claude/commands/speckit.constitution.md`
- `$speckit-specify` -> `.claude/commands/speckit.specify.md`
- `$speckit-clarify` -> `.claude/commands/speckit.clarify.md`
- `$speckit-plan` -> `.claude/commands/speckit.plan.md`
- `$speckit-tasks` -> `.claude/commands/speckit.tasks.md`
- `$speckit-analyze` -> `.claude/commands/speckit.analyze.md`
- `$speckit-checklist` -> `.claude/commands/speckit.checklist.md`
- `$speckit-implement` -> `.claude/commands/speckit.implement.md`
- `$speckit-taskstoissues` -> `.claude/commands/speckit.taskstoissues.md`

## Rules
- Use the matching Claude command file as the workflow source of truth for each skill.
- Prefer the local PowerShell helpers in `.specify/scripts/powershell/` for this repository.
- Preserve `.claude/commands/` when extending or updating the Codex integration.
