using System.Text.Json;
using MediatR;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.DevQa;

public interface IMeetingTranscriptPreviewService
{
    Task<MeetingTranscriptPreviewResult?> PreviewAsync(Guid meetingId, CancellationToken cancellationToken = default);
}

public sealed record MeetingTranscriptPreviewResult(
    Guid OrganizationId,
    Guid MeetingId,
    string FullText,
    string SegmentsJson,
    string SttModel,
    MeetingTranscriptCompletenessStatus CompletenessStatus,
    IReadOnlyList<string> Warnings,
    int ExpectedAudioFragmentCount,
    int TranscribedAudioFragmentCount,
    int RetryableFailedAudioFragmentCount,
    int TerminalFailedAudioFragmentCount,
    DateTime GeneratedAtUtc,
    Guid? ExistingTranscriptId,
    string? ExistingTranscriptHash,
    int? ExistingTranscriptRevision,
    string? ExistingFullText,
    string? ExistingSegmentsJson,
    string PreviewTranscriptHash,
    int PreviewTranscriptRevision,
    bool WouldChangeExistingTranscript);

public sealed class MeetingTranscriptPreviewService(
    ApplicationDbContext dbContext,
    ISttService sttService,
    ILogger<GenerateMeetingTranscriptJob> logger,
    IQaSttFailureInjectionService? qaSttFailureInjectionService = null) : IMeetingTranscriptPreviewService
{
    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly ISttService _sttService = sttService;
    private readonly ILogger<GenerateMeetingTranscriptJob> _logger = logger;
    private readonly IQaSttFailureInjectionService? _qaSttFailureInjectionService = qaSttFailureInjectionService;

    public async Task<MeetingTranscriptPreviewResult?> PreviewAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        var meeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Id == meetingId)
            .Select(x => new { x.Id, x.OrganizationId })
            .FirstOrDefaultAsync(cancellationToken);

        if (meeting is null)
        {
            return null;
        }

        var existingSnapshot = await LoadTranscriptSnapshotAsync(meetingId, meeting.OrganizationId, cancellationToken);

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var transactionRolledBack = false;
            try
            {
                var transcriptJob = new GenerateMeetingTranscriptJob(
                    _dbContext,
                    _sttService,
                    new NoOpPublisher(),
                    _logger,
                    postMeetingProcessingTracker: null,
                    qaSttFailureInjectionService: _qaSttFailureInjectionService);

                await transcriptJob.RunAsync(meetingId, meeting.OrganizationId, pipelineGenerationId: null, cancellationToken);

                var candidate = await _dbContext.MeetingTranscripts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.OrganizationId == meeting.OrganizationId,
                        cancellationToken);

                if (candidate is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    transactionRolledBack = true;
                    _dbContext.ChangeTracker.Clear();
                    return null;
                }

                var warnings = DeserializeWarnings(candidate.WarningsJson);
                var wouldChangeExistingTranscript = existingSnapshot is null
                                                    || !string.Equals(existingSnapshot.FullText, candidate.FullText, StringComparison.Ordinal)
                                                    || !string.Equals(existingSnapshot.SegmentsJson, candidate.SegmentsJson, StringComparison.Ordinal)
                                                    || existingSnapshot.TranscriptHash != candidate.TranscriptHash
                                                    || existingSnapshot.TranscriptRevision != candidate.TranscriptRevision;

                var result = new MeetingTranscriptPreviewResult(
                    meeting.OrganizationId,
                    meetingId,
                    candidate.FullText,
                    candidate.SegmentsJson,
                    candidate.SttModel,
                    candidate.CompletenessStatus,
                    warnings,
                    candidate.ExpectedAudioFragmentCount,
                    candidate.TranscribedAudioFragmentCount,
                    candidate.RetryableFailedAudioFragmentCount,
                    candidate.TerminalFailedAudioFragmentCount,
                    candidate.GeneratedAtUtc,
                    existingSnapshot?.Id,
                    existingSnapshot?.TranscriptHash,
                    existingSnapshot?.TranscriptRevision,
                    existingSnapshot?.FullText,
                    existingSnapshot?.SegmentsJson,
                    candidate.TranscriptHash,
                    candidate.TranscriptRevision,
                    wouldChangeExistingTranscript);

                await transaction.RollbackAsync(cancellationToken);
                transactionRolledBack = true;
                _dbContext.ChangeTracker.Clear();

                var persistedAfterRollback = await LoadTranscriptSnapshotAsync(
                    meetingId,
                    meeting.OrganizationId,
                    cancellationToken);

                if (existingSnapshot is null)
                {
                    if (persistedAfterRollback is not null)
                    {
                        throw new InvalidOperationException("Expected no persisted meeting transcript after preview rollback.");
                    }
                }
                else if (!SnapshotsMatch(existingSnapshot, persistedAfterRollback))
                {
                    throw new InvalidOperationException("Persisted meeting transcript changed after preview rollback.");
                }

                return result;
            }
            catch
            {
                if (!transactionRolledBack)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                _dbContext.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<TranscriptSnapshot?> LoadTranscriptSnapshotAsync(
        Guid meetingId,
        Guid organizationId,
        CancellationToken cancellationToken)
        => await _dbContext.MeetingTranscripts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .Select(x => new TranscriptSnapshot(
                x.Id,
                x.FullText,
                x.SegmentsJson,
                x.TranscriptHash,
                x.TranscriptRevision))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool SnapshotsMatch(TranscriptSnapshot expected, TranscriptSnapshot? actual)
        => actual is not null
           && actual.Id == expected.Id
           && string.Equals(actual.FullText, expected.FullText, StringComparison.Ordinal)
           && string.Equals(actual.SegmentsJson, expected.SegmentsJson, StringComparison.Ordinal)
           && string.Equals(actual.TranscriptHash, expected.TranscriptHash, StringComparison.Ordinal)
           && actual.TranscriptRevision == expected.TranscriptRevision;

    private static IReadOnlyList<string> DeserializeWarnings(string? warningsJson)
    {
        if (string.IsNullOrWhiteSpace(warningsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(warningsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record TranscriptSnapshot(
        Guid Id,
        string FullText,
        string SegmentsJson,
        string TranscriptHash,
        int TranscriptRevision);

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }
}
