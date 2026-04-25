# Transcript Endpoints Contract — REMOVED in Phase 4

**Status**: Removed in the 2026-04-21 scope revision.

Phase 4 no longer exposes a transcript endpoint. Live captions are delivered directly from the realtime platform (LiveKit Cloud) to connected clients over its native data channels; the backend is not on the caption path. The authoritative transcript for a meeting is produced in **Phase 6 (Post-Meeting AI Pipeline)** by an offline STT (WhisperX or equivalent) applied to the recording that Phase 4 hands off.

Any transcript retrieval API belongs to Phase 6 and MUST be specified in that phase's contracts — not here. This file is retained only as a forwarding marker so readers coming from the previous revision understand the endpoint was intentionally removed, not lost.

See:

- [../spec.md](../spec.md) — revised Phase 4 spec (FR-011, FR-012, Clarifications 2026-04-21).
- `docs/implementation-plan.md` — Phase 6 section for the post-meeting transcript pipeline.
