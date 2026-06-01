using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Rag;

public sealed class ReindexMeetingKnowledgeTests(MeetingAssistantWebFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Reindex_publishes_final_generation_for_transcript_summary_action_items_and_confirmed_tags()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meetingId = await SeedMeetingArtifactsAsync(db);

        var job = scope.ServiceProvider.GetRequiredService<ReindexMeetingKnowledgeJob>();
        await job.RunAsync(meetingId, TestOrganizationId, CancellationToken.None);

        var documents = await LoadCurrentDocumentsAsync(db, meetingId);

        documents.Select(x => x.ArtifactType).Should().BeEquivalentTo([
            KnowledgeArtifactType.Transcript,
            KnowledgeArtifactType.Summary,
            KnowledgeArtifactType.ActionItem,
            KnowledgeArtifactType.ConfirmedMeetingTags
        ]);
        documents.Select(x => x.IndexGenerationId).Distinct().Should().ContainSingle();
        documents.Should().OnlyContain(x => x.Visibility == KnowledgeVisibility.Published && x.IsCurrent);
        documents.Should().OnlyContain(x => x.ContentHash.Length == 64);

        var transcript = documents.Single(x => x.ArtifactType == KnowledgeArtifactType.Transcript);
        transcript.Chunks.Should().HaveCountGreaterThan(1, "long transcripts should be indexed as chunks");
        transcript.Chunks.Select(x => x.ChunkIndex).Should().Equal(Enumerable.Range(0, transcript.Chunks.Count));

        var allChunks = documents.SelectMany(x => x.Chunks).ToList();
        allChunks.Should().OnlyContain(x => x.Visibility == KnowledgeVisibility.Published && x.IsCurrent);
        allChunks.Should().OnlyContain(x => x.ContentHash.Length == 64);
        allChunks.Should().OnlyContain(x => x.EmbeddingVectorText.StartsWith("[", StringComparison.Ordinal));
        allChunks.Should().OnlyContain(x => x.Tags.Count == 1);
        allChunks.SelectMany(x => x.Tags).Should().OnlyContain(x => x.TagNameSnapshot == "Roadmap" && x.TagColorSnapshot == "#336699");

        var actionItem = documents.Single(x => x.ArtifactType == KnowledgeArtifactType.ActionItem);
        actionItem.Chunks.Single().Text.Should().Contain("Action item: Send launch notes");

        var tagDocument = documents.Single(x => x.ArtifactType == KnowledgeArtifactType.ConfirmedMeetingTags);
        tagDocument.Chunks.Single().Text.Should().Contain("Confirmed meeting tags: Roadmap (#336699)");

        var step = await db.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing);
        step.Status.Should().Be(PostMeetingProcessingStatus.Completed);
        step.ArtifactType.Should().Be("knowledge_document");
        step.ArtifactIdsJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Reindex_is_idempotent_for_current_rows_and_preserves_other_artifact_types_when_transcript_changes()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meetingId = await SeedMeetingArtifactsAsync(db);
        var job = scope.ServiceProvider.GetRequiredService<ReindexMeetingKnowledgeJob>();

        await job.RunAsync(meetingId, TestOrganizationId, CancellationToken.None);
        var totalDocumentsAfterFirstRun = await db.KnowledgeDocuments
            .IgnoreQueryFilters()
            .CountAsync(x => x.MeetingId == meetingId);
        var totalChunksAfterFirstRun = await db.KnowledgeChunks
            .IgnoreQueryFilters()
            .CountAsync(x => x.MeetingId == meetingId);

        await job.RunAsync(meetingId, TestOrganizationId, CancellationToken.None);

        (await db.KnowledgeDocuments.IgnoreQueryFilters().CountAsync(x => x.MeetingId == meetingId))
            .Should().Be(totalDocumentsAfterFirstRun);
        (await db.KnowledgeChunks.IgnoreQueryFilters().CountAsync(x => x.MeetingId == meetingId))
            .Should().Be(totalChunksAfterFirstRun);

        var afterSecondRun = await LoadCurrentDocumentsAsync(db, meetingId);
        afterSecondRun.Should().HaveCount(4);
        afterSecondRun
            .GroupBy(x => new { x.ArtifactType, x.ArtifactId, x.ArtifactVersion })
            .Should()
            .OnlyContain(group => group.Count() == 1);

        var transcript = await db.MeetingTranscripts
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId);
        transcript.FullText = "[00:00:00 Alice] Transcript regenerated with a new launch risk.";
        transcript.GeneratedAtUtc = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        await job.RunAsync(meetingId, TestOrganizationId, CancellationToken.None);

        var current = await LoadCurrentDocumentsAsync(db, meetingId);
        current.Select(x => x.ArtifactType).Should().BeEquivalentTo([
            KnowledgeArtifactType.Transcript,
            KnowledgeArtifactType.Summary,
            KnowledgeArtifactType.ActionItem,
            KnowledgeArtifactType.ConfirmedMeetingTags
        ]);
        current
            .GroupBy(x => new { x.ArtifactType, x.ArtifactId, x.ArtifactVersion })
            .Should()
            .OnlyContain(group => group.Count() == 1);
        current.Single(x => x.ArtifactType == KnowledgeArtifactType.Transcript)
            .Chunks.Single().Text.Should().Contain("Transcript regenerated");

        var archivedTranscriptCount = await db.KnowledgeDocuments
            .IgnoreQueryFilters()
            .CountAsync(x => x.MeetingId == meetingId
                             && x.ArtifactType == KnowledgeArtifactType.Transcript
                             && x.Visibility == KnowledgeVisibility.Archived
                             && !x.IsCurrent);
        archivedTranscriptCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reindex_failure_does_not_publish_drafts_and_marks_knowledge_indexing_failed()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meetingId = await SeedMeetingArtifactsAsync(db);

        var successfulJob = scope.ServiceProvider.GetRequiredService<ReindexMeetingKnowledgeJob>();
        await successfulJob.RunAsync(meetingId, TestOrganizationId, CancellationToken.None);
        var currentBeforeFailure = await LoadCurrentDocumentsAsync(db, meetingId);

        var summary = await db.MeetingSummaries
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId);
        summary.SummaryText = "The team reviewed roadmap launch milestones, risks, and a newly added blocker.";
        summary.GeneratedAtUtc = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        var failingService = new ReindexMeetingKnowledgeService(
            db,
            new ThrowingEmbeddingService(),
            NullLogger<ReindexMeetingKnowledgeService>.Instance);
        var failingJob = new ReindexMeetingKnowledgeJob(
            failingService,
            NullLogger<ReindexMeetingKnowledgeJob>.Instance,
            new PostMeetingProcessingTracker(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingJob.RunAsync(meetingId, TestOrganizationId, CancellationToken.None));

        var currentAfterFailure = await LoadCurrentDocumentsAsync(db, meetingId);
        currentAfterFailure.Select(x => x.Id).Should().BeEquivalentTo(currentBeforeFailure.Select(x => x.Id));

        var publishedDrafts = await db.KnowledgeChunks
            .IgnoreQueryFilters()
            .CountAsync(x => x.MeetingId == meetingId
                             && x.Visibility == KnowledgeVisibility.Draft
                             && x.IsCurrent);
        publishedDrafts.Should().Be(0);

        var step = await db.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing);
        step.Status.Should().Be(PostMeetingProcessingStatus.Failed);
        step.ErrorCode.Should().Be("knowledge_indexing_failed");
    }

    private async Task<Guid> SeedMeetingArtifactsAsync(ApplicationDbContext db)
    {
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            Title = "Launch Planning",
            ScheduledStartUtc = DateTime.UtcNow.AddHours(-2),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(-1),
            Status = MeetingStatus.Completed
        };

        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            Name = "Roadmap",
            Color = "#336699",
            IsActive = true
        };

        db.Meetings.Add(meeting);
        db.MeetingTags.Add(tag);
        db.MeetingMeetingTags.Add(new MeetingMeetingTag
        {
            MeetingId = meeting.Id,
            MeetingTagId = tag.Id
        });

        var transcriptText = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 90).Select(i => $"[00:{i / 60:00}:{i % 60:00} Alice] Roadmap launch discussion point {i} with enough detail for chunking."));

        db.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            MeetingId = meeting.Id,
            FullText = transcriptText,
            SegmentsJson = "[]",
            SttModel = "whisper-test",
            GeneratedAtUtc = DateTime.UtcNow
        });

        db.MeetingSummaries.Add(new MeetingSummary
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            MeetingId = meeting.Id,
            SummaryText = "The team reviewed roadmap launch milestones and risks.",
            LlmModel = "local-test",
            GeneratedAtUtc = DateTime.UtcNow
        });

        db.ActionItems.Add(new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            MeetingId = meeting.Id,
            Title = "Send launch notes",
            Description = "Alice sends launch notes to the team.",
            AssignedToUserId = TestUserId,
            DueDateUtc = DateTime.UtcNow.AddDays(2),
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private static async Task<List<KnowledgeDocument>> LoadCurrentDocumentsAsync(ApplicationDbContext db, Guid meetingId)
    {
        return await db.KnowledgeDocuments
            .IgnoreQueryFilters()
            .Include(x => x.Chunks)
            .ThenInclude(x => x.Tags)
            .Where(x => x.MeetingId == meetingId && x.Visibility == KnowledgeVisibility.Published && x.IsCurrent)
            .OrderBy(x => x.ArtifactType)
            .ThenBy(x => x.Title)
            .ToListAsync();
    }

    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public EmbeddingMetadata Metadata { get; } = new("deterministic-test", "failing-test", 3);

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated embedding failure");

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated embedding failure");
    }
}
