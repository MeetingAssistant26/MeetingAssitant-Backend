using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using tests.Integration.LiveSession;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public sealed class ExtractActionItemsJobPromptTests
{
    [Fact]
    public async Task RunAsync_UsesAiWorkTaskPromptAndMapsTaskSchemaToActionItem()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset by 2026-06-10T15:30:00+02:00.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService("""
            [{"task":"review the dataset","responsible_person":"Alice","deadline":"2026-06-10T15:30:00+02:00"}]
            """);
        var job = CreateJob(db, llm);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        llm.LastRequest!.Model.Should().Be("openai-compatible-local");
        var message = llm.LastRequest!.Messages.Should().ContainSingle().Subject;
        message.Content.Should().Contain("You are an AI meeting assistant specialized in extracting action items from meeting transcripts.");
        message.Content.Should().Contain("Meeting reference date (UTC):");
        message.Content.Should().Contain("Timezone for deadline normalization: UTC");
        message.Content.Should().Contain("Meeting participants:");
        message.Content.Should().Contain("Organization members:");
        message.Content.Should().Contain($"display_name=Alice");
        message.Content.Should().Contain($"Transcript:{Environment.NewLine}[00:00:01 Alice] I will review the dataset by 2026-06-10T15:30:00+02:00.");
        message.Content.Should().NotContain("{transcript}");
        message.Content.Should().NotContain("{meeting_context}");
        message.Content.Should().NotContain("{people_context}");
        llm.LastRequest.ResponseFormat.Should().BeNull();

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.Title.Should().Be("review the dataset");
        item.Description.Should().Contain("AI assignee: Alice");
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().Be(new DateTime(2026, 6, 10, 13, 30, 0, DateTimeKind.Utc));
        item.Status.Should().Be(ActionItemStatus.PendingReview);
        item.SyncMissingAssigneeReason.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_EnqueuesPersonalizedSummaryGenerationAfterActionExtractionCompletes()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"review the dataset","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_WithNoAmbientTenant_ShouldNotCrashAndShouldEnqueuePersonalizedSummaryGeneration()
    {
        await using var db = await LiveSessionTestDb.CreateWithAmbientTenantAsync(ambientTenantOrganizationId: null);
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"review the dataset","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .NotThrowAsync();

        var item = await db.DbContext.ActionItems
            .IgnoreQueryFilters()
            .SingleAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId);
        item.Title.Should().Be("review the dataset");
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));

        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Completed);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_WithMismatchedAmbientTenant_ShouldUseExplicitOrganizationScope()
    {
        var organizationId = Guid.NewGuid();
        var unrelatedAmbientTenantId = Guid.NewGuid();
        await using var db = await LiveSessionTestDb.CreateWithAmbientTenantAsync(unrelatedAmbientTenantId, organizationId);
        db.SeedOrganization("target", organizationId);
        db.SeedOrganization("ambient", unrelatedAmbientTenantId);
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var llm = new FakeLlmService("[]");
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, llm, tracker, jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Completed
            && x.ErrorCode == null);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending);
    }

    [Fact]
    public async Task RunAsync_WhenLlmFails_ShouldMarkActionExtractionFailedAndEnqueuePersonalizedSummaryFallback()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, new ThrowingLlmService(new InvalidOperationException("LLM unavailable")), tracker, jobs);

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM unavailable");

        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Failed
            && x.ErrorCode == "action_extraction_failed"
            && x.ErrorMessage == "LLM unavailable");
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_EnqueuesPersonalizedSummaryGenerationWhenExtractedTasksAreUnusable()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We did not identify a concrete task.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"   ","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        db.DbContext.ActionItems.Should().BeEmpty();
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tasks\":null}")]
    public async Task RunAsync_MalformedJsonFailsJobInsteadOfBeingTreatedAsNoTasks(string llmContent)
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService(llmContent));

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        db.DbContext.ActionItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ValidEmptyTasksDoesNotFailOrPersistRows()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("[]"));

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .NotThrowAsync();

        db.DbContext.ActionItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PersistsMissingAssigneeAsReviewRequiredAndSuppressesAutoSyncEligibility()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Someone should prepare the release notes.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the release notes","responsible_person":null,"deadline":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToParticipantId.Should().BeNull();
        item.AssignedToUserId.Should().BeNull();
        item.Status.Should().Be(ActionItemStatus.PendingReview);
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.NeedsAssignee);
    }

    [Fact]
    public async Task RunAsync_InvalidDueDateWithKnownAssigneePersistsExplicitReviewBlocker()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan by not-a-date.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the deployment plan","responsible_person":"Alice","deadline":"not-a-date"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().BeNull();
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.InvalidDueDate);
        item.Description.Should().Contain("AI due date: not-a-date (could not parse to UTC)");
    }

    [Fact]
    public async Task RunAsync_UnknownAssigneeAndInvalidDueDateRemainReviewableWithRawLlmContext()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Layla will prepare the deployment plan by not-a-date.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the deployment plan","responsible_person":"Layla","deadline":"not-a-date"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().BeNull();
        item.DueDateUtc.Should().BeNull();
        item.SyncMissingAssigneeReason.Should().Be(
            $"{ActionItemReviewReasons.NeedsAssignee};{ActionItemReviewReasons.InvalidDueDate}");
        item.Description.Should().Contain("AI assignee: Layla");
        item.Description.Should().Contain("AI due date: not-a-date (could not parse to UTC)");
    }

    [Fact]
    public async Task RunAsync_WhenExistingActionItemsMatchTranscriptSource_ShouldSkipExtraction()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        const string fullText = "[00:00:01 Alice] Existing action item context.";
        var transcript = new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = fullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        };
        TranscriptSourceIdentity.InitializeNew(transcript, fullText);
        db.DbContext.MeetingTranscripts.Add(transcript);
        var identity = TranscriptSourceIdentity.From(transcript);
        var existing = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "existing task",
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow
        };
        TranscriptSourceIdentity.ApplySourceFields(existing, identity);
        db.DbContext.ActionItems.Add(existing);
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService("[]");
        var job = CreateJob(db, llm);
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().BeNull();
        db.DbContext.ActionItems.Count(x => x.MeetingId == meetingId && x.SupersededAtUtc == null).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenExistingActionItemsAreFromOlderTranscriptSource_ShouldSupersedePendingAndExtractFreshItems()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        const string fullText = "[00:00:01 Alice] Updated transcript context.";
        var transcript = new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = fullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test",
            TranscriptHash = TranscriptSourceIdentity.ComputeHash(fullText),
            TranscriptRevision = 2
        };
        db.DbContext.MeetingTranscripts.Add(transcript);
        var stale = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "stale task",
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow,
            SourceTranscriptHash = "old-hash",
            SourceTranscriptRevision = 1
        };
        db.DbContext.ActionItems.Add(stale);
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"fresh task","responsible_person":"Alice","deadline":null}]
                """));
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var staleReloaded = await db.DbContext.ActionItems.SingleAsync(x => x.Id == stale.Id);
        staleReloaded.SupersededAtUtc.Should().NotBeNull();
        staleReloaded.SupersededReason.Should().Be("source_transcript_replaced");

        var active = await db.DbContext.ActionItems
            .Where(x => x.MeetingId == meetingId && x.SupersededAtUtc == null)
            .ToListAsync();
        active.Should().ContainSingle();
        active[0].Title.Should().Be("fresh task");
        active[0].SourceTranscriptHash.Should().Be(transcript.TranscriptHash);
    }

    [Fact]
    public async Task RunAsync_WhenLlmReturnsEmptyListAfterTranscriptReplacement_ShouldSupersedePendingAndLeaveNoActiveItems()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        const string fullText = "[00:00:01 Alice] Updated transcript context.";
        var transcript = new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = fullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test",
            TranscriptHash = TranscriptSourceIdentity.ComputeHash(fullText),
            TranscriptRevision = 2
        };
        db.DbContext.MeetingTranscripts.Add(transcript);
        var stale = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "stale task",
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow,
            SourceTranscriptHash = "old-hash",
            SourceTranscriptRevision = 1
        };
        db.DbContext.ActionItems.Add(stale);
        await db.DbContext.SaveChangesAsync();

        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, new FakeLlmService("[]"), tracker);
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var staleReloaded = await db.DbContext.ActionItems.SingleAsync(x => x.Id == stale.Id);
        staleReloaded.SupersededAtUtc.Should().NotBeNull();
        staleReloaded.SupersededReason.Should().Be("source_transcript_replaced");

        (await db.DbContext.ActionItems
            .Where(x => x.MeetingId == meetingId && x.SupersededAtUtc == null)
            .ToListAsync()).Should().BeEmpty();

        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Completed
            && x.ErrorCode == null);
    }

    [Fact]
    public async Task RunAsync_WhenLlmFailsDuringSourceChange_ShouldNotSupersedeExistingItems()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        const string fullText = "[00:00:01 Alice] Updated transcript context.";
        var transcript = new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = fullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test",
            TranscriptHash = TranscriptSourceIdentity.ComputeHash(fullText),
            TranscriptRevision = 2
        };
        db.DbContext.MeetingTranscripts.Add(transcript);
        var stale = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "stale task",
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow,
            SourceTranscriptHash = "old-hash"
        };
        db.DbContext.ActionItems.Add(stale);
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new ThrowingLlmService(new InvalidOperationException("llm failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            job.RunAsync(meetingId, organizationId, CancellationToken.None));

        var reloaded = await db.DbContext.ActionItems.SingleAsync(x => x.Id == stale.Id);
        reloaded.SupersededAtUtc.Should().BeNull();
        db.DbContext.ActionItems.Count(x => x.MeetingId == meetingId).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenReviewedOrSyncedItemsAreStale_ShouldPreserveThem()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        const string fullText = "[00:00:01 Alice] Updated transcript context.";
        var transcript = new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = fullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test",
            TranscriptHash = TranscriptSourceIdentity.ComputeHash(fullText),
            TranscriptRevision = 2
        };
        db.DbContext.MeetingTranscripts.Add(transcript);
        var approved = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "approved stale",
            Status = ActionItemStatus.Approved,
            ExtractedAtUtc = DateTime.UtcNow,
            SourceTranscriptHash = "old-hash"
        };
        var synced = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            Title = "synced stale",
            Status = ActionItemStatus.Synced,
            ExtractedAtUtc = DateTime.UtcNow,
            SyncedAtUtc = DateTime.UtcNow,
            ExternalTaskId = "ext-1",
            SourceTranscriptHash = "old-hash"
        };
        db.DbContext.ActionItems.AddRange(approved, synced);
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("[]"));
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        (await db.DbContext.ActionItems.SingleAsync(x => x.Id == approved.Id)).SupersededAtUtc.Should().BeNull();
        (await db.DbContext.ActionItems.SingleAsync(x => x.Id == synced.Id)).SupersededAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_RejectsRandomSuggestedUserIdWhenConfidenceIsHigh()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Layla will prepare the deployment plan.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var randomUserId = Guid.NewGuid();
        var job = CreateJob(db, new FakeLlmService($$"""
            [{"task":"prepare the deployment plan","responsible_person":"Layla","assigned_user_id":"{{randomUserId}}","assignee_confidence":0.95,"deadline":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().BeNull();
        item.AiSuggestedAssignedToUserId.Should().Be(randomUserId);
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.NeedsAssignee);
    }

    [Fact]
    public async Task RunAsync_LowAssigneeConfidenceDoesNotAutoAcceptSuggestedUserId()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.UserOrgMemberships.Add(new MeetingAssistant.Features.Organizations.Models.UserOrgMembership
        {
            OrganizationId = organizationId,
            UserId = aliceId,
            OrgRole = MeetingAssistant.Features.Organizations.Models.OrganizationRole.Member,
            IsEnabled = true
        });
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService($$"""
            [{"task":"prepare the deployment plan","responsible_person":"Alice","assigned_user_id":"{{aliceId}}","assignee_confidence":0.40,"deadline":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().Be(aliceId);
        item.AiSuggestedAssignedToUserId.Should().Be(aliceId);
        item.AiAssigneeConfidence.Should().Be(0.40m);
        item.SyncMissingAssigneeReason.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_LowDeadlineConfidenceMarksInvalidDueDateWithoutLegacyFallback()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan by 2026-06-10T15:30:00+02:00.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the deployment plan","responsible_person":"Alice","deadline":"2026-06-10T15:30:00+02:00","deadline_date":"2026-06-10","deadline_confidence":0.40,"assignee_confidence":0.95,"assigned_user_id":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.DueDateUtc.Should().BeNull();
        item.AiDeadlineConfidence.Should().Be(0.40m);
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.InvalidDueDate);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public async Task RunAsync_InvalidAssigneeConfidenceIsStoredAsNullAndDoesNotAutoAccept(decimal invalidConfidence)
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.UserOrgMemberships.Add(new MeetingAssistant.Features.Organizations.Models.UserOrgMembership
        {
            OrganizationId = organizationId,
            UserId = aliceId,
            OrgRole = MeetingAssistant.Features.Organizations.Models.OrganizationRole.Member,
            IsEnabled = true
        });
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Someone will prepare the deployment plan.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService($$"""
            [{"task":"prepare the deployment plan","responsible_person":null,"assigned_user_id":"{{aliceId}}","assignee_confidence":{{invalidConfidence}},"deadline":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().BeNull();
        item.AiSuggestedAssignedToUserId.Should().Be(aliceId);
        item.AiAssigneeConfidence.Should().BeNull();
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.NeedsAssignee);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-0.5)]
    public async Task RunAsync_InvalidDeadlineConfidenceIsStoredAsNullAndDoesNotAutoAccept(decimal invalidConfidence)
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService($$"""
            [{"task":"prepare the deployment plan","responsible_person":"Alice","deadline":"2026-06-10T15:30:00+02:00","deadline_date":"2026-06-10","deadline_utc":"2026-06-10T00:00:00Z","deadline_confidence":{{invalidConfidence}},"assignee_confidence":0.95,"assigned_user_id":"{{aliceId}}"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().BeNull();
        item.AiDeadlineConfidence.Should().BeNull();
        item.AiSuggestedDueDateUtc.Should().Be(new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.InvalidDueDate);
    }

    [Fact]
    public async Task RunAsync_LongAuditTextAndReasonsAreTruncatedWithoutPersistenceFailure()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var longAssignee = new string('A', 250);
        var longDeadline = new string('D', 250);
        var longAssigneeReason = new string('R', 600);
        var longDeadlineReason = new string('E', 600);
        var expectedAssignee = longAssignee[..200];
        var expectedDeadline = longDeadline[..200];
        var expectedAssigneeReason = longAssigneeReason[..500];
        var expectedDeadlineReason = longDeadlineReason[..500];

        var job = CreateJob(db, new FakeLlmService($$"""
            [{"task":"prepare the deployment plan","responsible_person":"{{longAssignee}}","deadline":"{{longDeadline}}","assignee_reason":"{{longAssigneeReason}}","deadline_reason":"{{longDeadlineReason}}","assignee_confidence":0.95,"deadline_confidence":0.95,"assigned_user_id":"{{aliceId}}","deadline_date":"2026-06-10","deadline_utc":"2026-06-10T00:00:00Z"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AiRawAssigneeText.Should().Be(expectedAssignee);
        item.AiRawDeadlineText.Should().Be(expectedDeadline);
        item.AiAssigneeResolutionReason.Should().Be(expectedAssigneeReason);
        item.AiDeadlineResolutionReason.Should().Be(expectedDeadlineReason);
        item.AiRawAssigneeText.Should().HaveLength(200);
        item.AiRawDeadlineText.Should().HaveLength(200);
        item.AiAssigneeResolutionReason.Should().HaveLength(500);
        item.AiDeadlineResolutionReason.Should().HaveLength(500);
        item.Description.Should().Contain($"AI assignee: {expectedAssignee}");
        item.Description.Should().Contain($"AI due date: {expectedDeadline}");
        item.Description.Should().Contain($"AI assignee reason: {expectedAssigneeReason}");
        item.Description.Should().Contain($"AI deadline reason: {expectedDeadlineReason}");
    }

    [Fact]
    public async Task RunAsync_BackwardCompatibleLegacySchemaStillMapsAssigneeAndDueDate()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will review the dataset by 2026-06-10T15:30:00+02:00.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"review the dataset","assignee":"Alice","due_date":"2026-06-10T15:30:00+02:00"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().Be(new DateTime(2026, 6, 10, 13, 30, 0, DateTimeKind.Utc));
        item.SyncMissingAssigneeReason.Should().BeNull();
    }

    private static ExtractActionItemsJob CreateJob(
        LiveSessionTestDb db,
        ILLMService llmService,
        IPostMeetingProcessingTracker? tracker = null,
        FakeBackgroundJobClient? backgroundJobClient = null)
    {
        return new ExtractActionItemsJob(
            db.DbContext,
            llmService,
            new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory)),
            Options.Create(new OpenAiCompatibleOptions
            {
                Llm = new OpenAiCompatibleOptions.ProviderConfig
                {
                    BaseUrl = "http://llm.test/v1",
                    ApiKey = "test-key",
                    Model = "openai-compatible-local"
                }
            }),
            NullLogger<ExtractActionItemsJob>.Instance,
            tracker,
            hangfireJobContextAccessor: null,
            backgroundJobClient);
    }

    private sealed class FakeLlmService(string content) : ILLMService
    {
        public LLMRequest? LastRequest { get; private set; }

        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new LLMResponse
            {
                Choices =
                [
                    new Choice
                    {
                        Message = new ChoiceMessage
                        {
                            Role = "assistant",
                            Content = content
                        }
                    }
                ]
            });
        }

        public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ExtractActionItemsJob should parse the ai_work tasks schema itself.");
    }

    private sealed class ThrowingLlmService(Exception exception) : ILLMService
    {
        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
            => Task.FromException<LLMResponse>(exception);

        public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ExtractActionItemsJob should parse the ai_work tasks schema itself.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
