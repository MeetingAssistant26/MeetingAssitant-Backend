# Tasks: Participant Audio Egress & Storage

**Input**: Design documents from `/specs/005-participant-audio-egress/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/
**Tests**: Included — the feature specification explicitly requires new integration tests for retry ceiling, late-arriving tracks, and queryable pipeline state.

---

## Phase 1: Setup (Verify Existing Infrastructure)

**Purpose**: Confirm the codebase is healthy before making surgical amendments.

- [ ] T001 Verify `dotnet build` succeeds at repo root and existing `ParticipantAudioHandoffTests` pass (`dotnet test --filter "FullyQualifiedName~ParticipantAudioHandoffTests"`)

---

## Phase 2: Foundational (DTOs, Interface, Test Harness)

**Purpose**: Create the shared types and test helpers needed by all user story phases.

**⚠️ CRITICAL**: No user story implementation can begin until this phase is complete.

- [ ] T002 [P] Create `PipelineStateView` and `ParticipantAudioTrackView` records in `MeetingAssistant/Features/LiveSession/Models/PipelineStateView.cs`
- [ ] T003 [P] Create `IPipelineStateService` interface in `MeetingAssistant/Features/LiveSession/Services/IPipelineStateService.cs`
- [ ] T004 [P] Add `SeedSessionEvent` helper to `tests/Integration/LiveSession/LiveSessionTestSupport.cs` for creating `ParticipantJoined` and `ParticipantAudioReady` events in tests

**Checkpoint**: Foundation ready — DTOs, interface, and test harness exist and compile.

---

## Phase 3: User Story 1 — Per-Participant Audio Capture (Priority: P1) 🎯 MVP

**Goal**: Ensure the existing per-participant audio capture path (webhook ingestion, track record creation, job enqueueing) remains intact after amendments.

**Independent Test**: Run the existing `EgressEndedWithMultipleFileResults_ShouldCreateTracksEnqueueTransfers_AndDispatchReadyOnce` test. It must pass.

- [ ] T005 [US1] Verify existing `EgressEndedWithMultipleFileResults_ShouldCreateTracksEnqueueTransfers_AndDispatchReadyOnce` test passes as baseline before any production edits
- [ ] T006 [P] [US1] Add integration test for unrecognized participant in `egress_ended` webhook — track not created, webhook acknowledged without failure in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`
- [ ] T006a [P] [US1] Add media-type filtering in `WebhookService.ProcessAsync` — only process `.ogg`/audio files from `FileResults`, skip video streams gracefully with a warning log in `MeetingAssistant/Features/LiveSession/Services/WebhookService.cs`
- [ ] T006b [P] [US1] Add integration test for mixed audio/video egress payload — assert only audio track rows created, video ignored in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`

**Checkpoint**: US1 baseline verified. Existing audio capture pipeline is stable.

---

## Phase 4: User Story 2 — Audio Tracks Land in Organization Storage Reliably (Priority: P1)

**Goal**: Harden the transfer job with Hangfire retry policy and an 8-minute hard ceiling so tracks always reach a terminal state within the 10-minute SLA.

**Independent Test**: Run the new ceiling and idempotency tests in `ParticipantAudioHandoffTests`. All must pass.

- [ ] T007 [US2] Add `[AutomaticRetry(Attempts = 3)]` attribute to `IngestParticipantAudioJob.RunAsync` in `MeetingAssistant/Features/LiveSession/Jobs/IngestParticipantAudioJob.cs`
- [ ] T008 [US2] Add 8-minute ceiling guard at entry of `IngestParticipantAudioJob.RunAsync` — if `DateTime.UtcNow - track.CreatedAtUtc > TimeSpan.FromMinutes(8)`, mark `Failed`, skip download/upload, and call `PersistTerminalStatusAndTryCreateReadyEventAsync` so the join barrier can fire if this was the last non-terminal track in `MeetingAssistant/Features/LiveSession/Jobs/IngestParticipantAudioJob.cs`
- [ ] T009 [P] [US2] Add integration test for 8-minute ceiling — stale track auto-Fails without download attempt in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`
- [ ] T010 [P] [US2] Add integration test for idempotent duplicate `egress_ended` webhook — no duplicate track records, uploads, or readiness events in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`
- [ ] T011 [US2] Add integration test for transient upload failure → retry path via `FakeStorageService.ThrowOnUpload` toggled off on second call in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`

**Checkpoint**: US2 hardened. Retry and ceiling guarantees are in place and tested.

---

## Phase 5: User Story 3 — Downstream Pipeline Knows When All Audio Is Ready (Priority: P2)

**Goal**: Expose queryable pipeline state and verify the readiness event behaves correctly for late-arriving tracks and missing participants.

**Independent Test**: Run the new `PipelineStateService` and barrier tests in `ParticipantAudioHandoffTests`. All must pass.

- [ ] T012 [US3] Implement `PipelineStateService` in `MeetingAssistant/Features/LiveSession/Services/PipelineStateService.cs` — all queries MUST go through `ApplicationDbContext` so existing global query filters on `OrganizationId` apply; no `OrganizationId` parameter on the interface, but the implementation inherits tenant scoping from the DbContext
- [ ] T013 [US3] Register `IPipelineStateService` / `PipelineStateService` in `MeetingAssistant/Features/LiveSession/LiveSessionDI.cs`
- [ ] T014 [US3] Add integration test for `PipelineStateService.GetStateAsync` — correct `ExpectedParticipantCount` (from `SessionEvents`), `AllTracksTerminal`, and `ReadyEventFired`; assert query execution time <500ms in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`
- [ ] T015 [US3] Add integration test for late-arriving track — readiness event does not re-fire after initial emission in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`
- [ ] T016 [US3] Add integration test for missing participant — barrier fires when existing tracks are terminal; expected count from `SessionEvents` is informational only in `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs`

**Checkpoint**: US3 complete. Pipeline state is queryable and the join barrier is verified for edge cases.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, documentation update, and migration guard.

- [ ] T017 [P] Run full `ParticipantAudioHandoffTests` suite and verify all tests pass (`dotnet test --filter "FullyQualifiedName~ParticipantAudioHandoffTests"`). Include a scale sanity check: seed 50 tracks for one meeting, process all concurrently, assert `ParticipantAudioReadyEvent` fires and barrier evaluation completes without timeout degradation
- [ ] T018 [P] Update `docs/implementation-plan.md` Phase 5 section to mark completion and link to `specs/005-participant-audio-egress/spec.md` as the authoritative spec
- [ ] T019 [P] Verify zero new migrations exist under `MeetingAssistant/Migrations/` (`git status --short MeetingAssistant/Migrations/` must be empty)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Stories (Phase 3–5)**: All depend on Foundational phase completion
  - User stories proceed in priority order: P1 (US1, US2) → P2 (US3)
  - US1 and US2 are both P1 and can be worked in parallel after Foundational
- **Polish (Phase 6)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) — verifies existing behavior baseline
- **User Story 2 (P1)**: Can start after Foundational (Phase 2) — hardens `IngestParticipantAudioJob`; independent of US1
- **User Story 3 (P2)**: Can start after Foundational (Phase 2) — adds `PipelineStateService`; independent of US1/US2 but may be validated alongside them

### Within Each User Story

- T005 (US1 baseline verification) must complete before T006 (US1 new test)
- T007 and T008 (US2 implementation) are sequential edits to the same file
- T009, T010, T011 (US2 tests) depend on T007–T008 implementation
- T012 (US3 service implementation) must complete before T013 (DI registration)
- T014, T015, T016 (US3 tests) depend on T012–T013 implementation

### Parallel Opportunities

- T002, T003, T004 (Phase 2) can run in parallel — different files
- T005 and T006 (US1) can run in parallel — one is verification, one is new test
- T009 and T010 (US2 tests) can run in parallel — different test methods in the same file, but sequential execution is safer for merge conflicts
- T014, T015, T016 (US3 tests) can run in parallel — different test methods
- T017, T018, T019 (Phase 6) can run in parallel

---

## Parallel Example: User Story 2

```bash
# Launch implementation tasks sequentially (same file):
Task: "Add [AutomaticRetry(Attempts = 3)] to IngestParticipantAudioJob.RunAsync"
Task: "Add 8-minute ceiling guard at entry of IngestParticipantAudioJob.RunAsync"

# Then launch tests:
Task: "Add integration test for 8-minute ceiling"
Task: "Add integration test for idempotent duplicate egress_ended webhook"
Task: "Add integration test for transient upload failure retry path"
```

---

## Implementation Strategy

### MVP First (User Stories 1 + 2)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (baseline verification)
4. Complete Phase 4: User Story 2 (retry + ceiling — the core amendment)
5. **STOP and VALIDATE**: Run all US1/US2 tests independently
6. Optionally demo the hardened pipeline

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → baseline confirmed
3. Add User Story 2 → Test independently → retry/ceiling hardened
4. Add User Story 3 → Test independently → operational visibility added
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1 (baseline + unrecognized participant test)
   - Developer B: User Story 2 (retry + ceiling + tests)
   - Developer C: User Story 3 (PipelineStateService + tests)
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing (TDD for new test cases)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
- **Zero new entities / zero new migrations**: all database work is read-only or uses existing schema
