using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using MeetingAssistant.Features.DevQa;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using static MeetingAssistant.Features.LiveSession.Services.MeetingTranscriptCompletenessGuard;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class GenerateMeetingTranscriptJob(
        ApplicationDbContext dbContext,
        ISttService sttService,
        IPublisher publisher,
        ILogger<GenerateMeetingTranscriptJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IQaSttFailureInjectionService? qaSttFailureInjectionService = null)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISttService _sttService = sttService;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<GenerateMeetingTranscriptJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IQaSttFailureInjectionService? _qaSttFailureInjectionService = qaSttFailureInjectionService;

        private const int MaxSttParallelism = 1;
        private const int PersistedTranscriptSegmentVersion = 3;
        private const string ParticipantSpeakerRole = "participant";
        private const string AssistantSpeakerRole = "assistant";
        private const string AssistantDisplayName = "AI Assistant";
        private const string EgressAudioSource = "egress_audio";
        private const string AiAssistantTraceSource = "ai_assistant_trace";
        private const string AiDebugTraceSttModel = "ai-debug-trace";
        private const int MaxSttAttemptsBeforeTerminal = 5;
        private const string SttFailureCodeFailed = "stt_failed";
        private const string SttFailureCodeNoSegments = "stt_no_segments";

        private static readonly Regex TrackSidFromObjectKeyRegex = new(
            @"(?:^|/)track-(?<sid>[^/.\\]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.Stt,
                    message: "STT transcription started.",
                    cancellationToken: cancellationToken);
            }

            var pendingFragmentCount = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .CountAsync(
                    x => x.MeetingId == meetingId
                         && x.OrganizationId == organizationId
                         && x.Status != ParticipantAudioFragmentStatus.Available
                         && x.Status != ParticipantAudioFragmentStatus.Failed,
                    cancellationToken);

            if (pendingFragmentCount > 0)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.RecordEventAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingEventType.Info,
                        PostMeetingProcessingStepType.Stt,
                        PostMeetingProcessingStatus.InProgress,
                        message: $"STT transcription is waiting for {pendingFragmentCount} participant audio fragment(s) to finish ingest.",
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Transcript generation deferred because participant audio fragments are still pending. MeetingId={MeetingId} PendingFragmentCount={PendingFragmentCount}",
                    meetingId,
                    pendingFragmentCount);
                return;
            }

            var allAvailableFragments = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId
                            && x.OrganizationId == organizationId
                            && x.Status == ParticipantAudioFragmentStatus.Available
                            && x.StorageObjectKey != null)
                .ToListAsync(cancellationToken);

            var fragments = allAvailableFragments
                .Where(x => x.SttStatus != ParticipantAudioFragmentSttStatus.FailedTerminal)
                .ToList();

            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(x => x.Id == meetingId && x.OrganizationId == organizationId)
                .Select(x => new { x.RoomActivatedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);

            var timingEvents = await _dbContext.SessionEvents
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId
                            && x.OrganizationId == organizationId
                            && (x.EventType == SessionEventType.RoomStarted
                                || x.EventType == SessionEventType.TrackPublished
                                || x.EventType == SessionEventType.ParticipantJoined))
                .Select(x => new TranscriptTimingEvent(
                    x.EventType,
                    x.ParticipantUserId,
                    x.ExternalEventId,
                    x.PayloadJson,
                    x.OccurredAtUtc))
                .ToListAsync(cancellationToken);

            var roomActivatedAtUtc = meeting?.RoomActivatedAtUtc
                ?? timingEvents
                    .Where(x => x.EventType == SessionEventType.RoomStarted)
                    .OrderBy(x => x.OccurredAtUtc)
                    .Select(x => (DateTime?)x.OccurredAtUtc)
                    .FirstOrDefault();

            var traceSegments = await LoadAiTraceTranscriptSegmentsAsync(
                meetingId,
                organizationId,
                roomActivatedAtUtc,
                cancellationToken);
            var assistantSegments = traceSegments.AssistantSegments;
            var participantTraceSegments = traceSegments.ParticipantSegments;

            if (fragments.Count == 0 && assistantSegments.Count == 0 && participantTraceSegments.Count == 0)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.Stt,
                        "no_available_fragments",
                        "No available participant audio fragments or assistant speech traces were found for transcript generation.",
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Transcript generation skipped because no available participant audio fragments or assistant speech traces were found. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var expectedAudioFragmentCount = allAvailableFragments.Count;

            var fragmentsWithTiming = fragments
                .Select(fragment => new FragmentTranscriptionInput(
                    fragment,
                    Timing: ResolveTrackTiming(
                        fragment.ParticipantUserId,
                        fragment.TrackSid,
                        fragment.StorageObjectKey!,
                        roomActivatedAtUtc,
                        fragment.TrackPublishedAtUtc,
                        fragment.EgressStartedAtUtc,
                        fragment.StorageAvailableAtUtc,
                        timingEvents)
                ))
                .OrderBy(x => x.Timing.RoomRelativeStartOffsetMs)
                .ThenBy(x => x.Fragment.ParticipantUserId)
                .ThenBy(x => x.Fragment.TrackSid, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Fragment.StorageObjectKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allSegments = new ConcurrentBag<NormalizedTranscriptSegment>();
            var sttModels = new ConcurrentBag<string>();
            var failedFragments = new ConcurrentBag<FailedFragmentTranscription>();
            var succeededFragmentIds = new ConcurrentBag<Guid>();

            await Parallel.ForEachAsync(
                fragmentsWithTiming,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    // ai_work STT is a CPU-bound local service and can fail under concurrent
                    // multipart transcription requests. Process fragments one at a time while
                    // still isolating failures per fragment.
                    MaxDegreeOfParallelism = MaxSttParallelism
                },
                async (track, ct) =>
                {
                    var fragment = track.Fragment;
                    try
                    {
                        MarkFragmentSttInProgress(fragment);

                        await _dbContext.SaveChangesAsync(ct);

                        _qaSttFailureInjectionService?.TryInjectFailure(
                            meetingId,
                            organizationId,
                            fragment.ParticipantUserId,
                            fragment.StorageObjectKey!);

                        var result = await _sttService.TranscribeTrackAsync(
                            fragment.ParticipantUserId,
                            fragment.StorageObjectKey!,
                            ct);

                        if (result.Segments.Count == 0)
                        {
                            MarkFragmentSttFailed(
                                fragment,
                                SttFailureCodeNoSegments,
                                "STT returned no transcript segments.");
                            await _dbContext.SaveChangesAsync(ct);

                            failedFragments.Add(new FailedFragmentTranscription(
                                fragment.Id,
                                fragment.SttFailureMessage ?? "STT returned no transcript segments."));

                            _logger.LogWarning(
                                "STT transcription returned no transcript segments for participant audio fragment. MeetingId={MeetingId} FragmentId={FragmentId} ParticipantUserId={ParticipantUserId} TrackSid={TrackSid}",
                                meetingId,
                                fragment.Id,
                                fragment.ParticipantUserId,
                                fragment.TrackSid);

                            return;
                        }

                        MarkFragmentSttSucceeded(fragment, result.Model, result.Segments.Count);
                        await _dbContext.SaveChangesAsync(ct);

                        sttModels.Add(result.Model);
                        succeededFragmentIds.Add(fragment.Id);
                        foreach (var segment in result.Segments)
                        {
                            allSegments.Add(NormalizeSegment(
                                segment,
                                fragment.ParticipantAudioTrackId,
                                fragment.Id,
                                roomActivatedAtUtc,
                                track.Timing));
                        }
                    }
                    catch (Exception ex)
                    {
                        var failureMessage = TruncateFailureMessage(ex.GetBaseException().Message);
                        MarkFragmentSttFailed(fragment, SttFailureCodeFailed, failureMessage);
                        await _dbContext.SaveChangesAsync(ct);

                        failedFragments.Add(new FailedFragmentTranscription(fragment.Id, failureMessage));

                        _logger.LogWarning(
                            ex,
                            "STT transcription failed for participant audio fragment. MeetingId={MeetingId} FragmentId={FragmentId} ParticipantUserId={ParticipantUserId} TrackSid={TrackSid}",
                            meetingId,
                            fragment.Id,
                            fragment.ParticipantUserId,
                            fragment.TrackSid);
                    }
                });

            var failedFragmentIds = failedFragments
                .Select(x => x.FragmentId)
                .Distinct()
                .ToList();

            if (allSegments.IsEmpty && assistantSegments.Count == 0 && participantTraceSegments.Count == 0)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.Stt,
                        "all_fragments_failed",
                        "All participant audio fragment transcriptions failed.",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: failedFragmentIds),
                        cancellationToken: cancellationToken);
                }

                _logger.LogWarning(
                    "Transcript generation skipped because all participant transcriptions failed. MeetingId={MeetingId}",
                    meetingId);

                if (failedFragmentIds.Count > 0)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                return;
            }

            var orderedHumanSegments = allSegments
                .OrderBy(x => x.RoomRelativeStartMs)
                .ThenBy(x => x.RoomRelativeEndMs)
                .ToList();
            var participantSegmentCount = orderedHumanSegments.Count > 0
                ? orderedHumanSegments.Count
                : participantTraceSegments.Count;

            var transcribedFragmentCount = succeededFragmentIds.Distinct().Count();
            var retryableFailedCount = allAvailableFragments.Count(fragment =>
                fragment.SttStatus == ParticipantAudioFragmentSttStatus.FailedRetryable);
            var terminalFailedCount = allAvailableFragments.Count(fragment =>
                fragment.SttStatus == ParticipantAudioFragmentSttStatus.FailedTerminal);
            var isTranscriptComplete = expectedAudioFragmentCount == 0
                                       || (failedFragmentIds.Count == 0
                                           && terminalFailedCount == 0
                                           && transcribedFragmentCount >= expectedAudioFragmentCount);
            var completenessStatus = isTranscriptComplete
                ? MeetingTranscriptCompletenessStatus.Complete
                : MeetingTranscriptCompletenessStatus.CompletedWithWarnings;
            var transcriptWarnings = BuildTranscriptWarnings(
                failedFragmentIds,
                retryableFailedCount,
                terminalFailedCount,
                expectedAudioFragmentCount,
                transcribedFragmentCount,
                completenessStatus);

            if (_postMeetingProcessingTracker is not null)
            {
                if (isTranscriptComplete)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.Stt,
                        message: $"STT transcription completed for {participantSegmentCount} participant segment(s).",
                        artifact: fragmentsWithTiming.Count > 0
                            ? new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: fragmentsWithTiming.Select(x => x.Fragment.Id).ToList())
                            : null,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _postMeetingProcessingTracker.CompleteStepWithWarningsAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.Stt,
                        message: $"STT transcription completed with warnings for {participantSegmentCount} participant segment(s); {failedFragmentIds.Count} audio fragment(s) remain untranscribed.",
                        artifact: failedFragmentIds.Count > 0
                            ? new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: failedFragmentIds)
                            : null,
                        cancellationToken: cancellationToken);

                    await _postMeetingProcessingTracker.RecordEventAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingEventType.Info,
                        PostMeetingProcessingStepType.Stt,
                        PostMeetingProcessingStatus.CompletedWithWarnings,
                        message: "Transcript coverage is degraded because one or more audio fragments failed STT.",
                        artifact: failedFragmentIds.Count > 0
                            ? new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: failedFragmentIds)
                            : null,
                        metadataJson: JsonSerializer.Serialize(transcriptWarnings),
                        cancellationToken: cancellationToken);
                }

                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.TranscriptPersistence,
                    message: "Transcript persistence started.",
                    cancellationToken: cancellationToken);
            }

            var participantIds = orderedHumanSegments
                .Select(x => x.ParticipantUserId)
                .Distinct()
                .ToList();

            var participantProfiles = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId && participantIds.Contains(x.UserId))
                .Select(x => new
                {
                    x.UserId,
                    x.User.DisplayName,
                    x.User.UserName
                })
                .ToListAsync(cancellationToken);

            var displayNames = participantProfiles.ToDictionary(
                x => x.UserId,
                x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.UserName ?? x.UserId.ToString() : x.DisplayName);

            var participantSegments = orderedHumanSegments.Count > 0
                ? orderedHumanSegments.Select(segment => CreateParticipantOutputSegment(segment, displayNames)).ToList()
                : participantTraceSegments;

            var orderedSegments = participantSegments
                .Concat(assistantSegments)
                .OrderBy(x => x.RoomRelativeStartMs)
                .ThenBy(x => x.RoomRelativeEndMs)
                .ThenBy(x => x.SortPriority)
                .ThenBy(x => x.SpeakerDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Text, StringComparer.Ordinal)
                .ToList();

            var fullText = string.Join(
                Environment.NewLine,
                orderedSegments.Select(segment => $"[{FormatTimestamp(segment.StartMs)} {segment.SpeakerDisplayName}] {segment.Text}"));

            var segmentsJson = JsonSerializer.Serialize(
                orderedSegments.Select(segment => new PersistedTranscriptSegment(
                    PersistedTranscriptSegmentVersion,
                    segment.SpeakerRole,
                    segment.ParticipantUserId,
                    segment.ParticipantAudioTrackId,
                    segment.ParticipantAudioFragmentId,
                    segment.TrackRelativeStartMs,
                    segment.TrackRelativeEndMs,
                    segment.RoomRelativeStartMs,
                    segment.RoomRelativeEndMs,
                    segment.AbsoluteStartUtc,
                    segment.AbsoluteEndUtc,
                    // Backward-compatible aliases used by existing read DTOs. For v2+ rows these are room-relative.
                    segment.RoomRelativeStartMs,
                    segment.RoomRelativeEndMs,
                    segment.Text,
                    segment.AvgLogProb,
                    segment.TimestampOffsetSource,
                    segment.SpeakerDisplayName,
                    segment.Source,
                    segment.TraceEventId,
                    segment.SessionId,
                    segment.TurnId)));

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            var transcriptChanged = transcript is null
                                    || !string.Equals(transcript.FullText, fullText, StringComparison.Ordinal)
                                    || !string.Equals(transcript.SegmentsJson, segmentsJson, StringComparison.Ordinal);

            if (transcript == null)
            {
                transcript = new MeetingTranscript
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId
                };

                _dbContext.MeetingTranscripts.Add(transcript);
            }

            var completenessChanged = transcript.CompletenessStatus != completenessStatus;
            if (transcriptChanged || completenessChanged)
            {
                transcript.FullText = fullText;
                transcript.SegmentsJson = segmentsJson;
                transcript.SttModel = sttModels.FirstOrDefault()
                                      ?? (participantTraceSegments.Count > 0 ? AiDebugTraceSttModel : string.Empty);
                transcript.GeneratedAtUtc = DateTime.UtcNow;
                transcript.CompletenessStatus = completenessStatus;
                transcript.ExpectedAudioFragmentCount = expectedAudioFragmentCount;
                transcript.TranscribedAudioFragmentCount = transcribedFragmentCount;
                transcript.RetryableFailedAudioFragmentCount = retryableFailedCount;
                transcript.TerminalFailedAudioFragmentCount = terminalFailedCount;
                transcript.MissingAudioFragmentIdsJson = JsonSerializer.Serialize(failedFragmentIds);
                transcript.WarningsJson = JsonSerializer.Serialize(transcriptWarnings);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (_postMeetingProcessingTracker is not null)
            {
                if (isTranscriptComplete)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.TranscriptPersistence,
                        message: "Transcript persisted.",
                        artifact: new PostMeetingArtifactLink("meeting_transcript", transcript.Id),
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _postMeetingProcessingTracker.CompleteStepWithWarningsAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.TranscriptPersistence,
                        message: "Degraded transcript persisted.",
                        artifact: new PostMeetingArtifactLink("meeting_transcript", transcript.Id),
                        cancellationToken: cancellationToken);

                    await _postMeetingProcessingTracker.CompleteRunWithWarningsAsync(
                        organizationId,
                        meetingId,
                        message: "Post-meeting processing completed with degraded transcript coverage.",
                        cancellationToken: cancellationToken);
                }
            }

            if (transcriptChanged && isTranscriptComplete)
            {
                await _publisher.Publish(
                    new MeetingTranscriptReadyEvent(meetingId, organizationId, DateTime.UtcNow),
                    cancellationToken);
            }
        }

        private static void MarkFragmentSttInProgress(ParticipantAudioFragment fragment)
        {
            var now = DateTime.UtcNow;
            fragment.SttStatus = ParticipantAudioFragmentSttStatus.InProgress;
            fragment.SttAttemptCount += 1;
            fragment.LastSttAttemptAtUtc = now;
            fragment.NextSttRetryAtUtc = null;
        }

        private static void MarkFragmentSttSucceeded(ParticipantAudioFragment fragment, string model, int segmentCount)
        {
            var now = DateTime.UtcNow;
            fragment.SttStatus = ParticipantAudioFragmentSttStatus.Succeeded;
            fragment.LastSttSucceededAtUtc = now;
            fragment.SttModel = model;
            fragment.SttSegmentCount = segmentCount;
            fragment.SttFailureCode = null;
            fragment.SttFailureMessage = null;
            fragment.LastSttFailedAtUtc = null;
            fragment.NextSttRetryAtUtc = null;
        }

        private static void MarkFragmentSttFailed(
            ParticipantAudioFragment fragment,
            string failureCode,
            string failureMessage)
        {
            var now = DateTime.UtcNow;
            var isTerminal = fragment.SttAttemptCount >= MaxSttAttemptsBeforeTerminal;
            fragment.SttStatus = isTerminal
                ? ParticipantAudioFragmentSttStatus.FailedTerminal
                : ParticipantAudioFragmentSttStatus.FailedRetryable;
            fragment.LastSttFailedAtUtc = now;
            fragment.SttFailureCode = failureCode;
            fragment.SttFailureMessage = TruncateFailureMessage(failureMessage);
            fragment.SttSegmentCount = 0;
            fragment.NextSttRetryAtUtc = null;
        }

        private static IReadOnlyList<string> BuildTranscriptWarnings(
            IReadOnlyList<Guid> failedFragmentIds,
            int retryableFailedCount,
            int terminalFailedCount,
            int expectedAudioFragmentCount,
            int transcribedFragmentCount,
            MeetingTranscriptCompletenessStatus completenessStatus)
        {
            var warnings = new List<string>();

            if (completenessStatus != MeetingTranscriptCompletenessStatus.Complete)
            {
                warnings.Add(
                    $"Degraded transcript: transcribed {transcribedFragmentCount} of {expectedAudioFragmentCount} expected audio fragment(s).");
            }

            if (retryableFailedCount > 0)
            {
                warnings.Add($"{retryableFailedCount} audio fragment(s) have retryable STT failures.");
            }

            if (terminalFailedCount > 0)
            {
                warnings.Add($"{terminalFailedCount} audio fragment(s) have terminal STT failures.");
            }

            if (failedFragmentIds.Count > 0)
            {
                warnings.Add($"Missing audio fragment ids: {string.Join(", ", failedFragmentIds)}.");
            }

            return warnings;
        }

        private static string FormatTimestamp(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        private static NormalizedTranscriptSegment NormalizeSegment(
            TranscriptSegment segment,
            Guid? participantAudioTrackId,
            Guid participantAudioFragmentId,
            DateTime? roomActivatedAtUtc,
            TrackTiming timing)
        {
            var roomRelativeStartMs = timing.RoomRelativeStartOffsetMs + segment.StartMs;
            var roomRelativeEndMs = timing.RoomRelativeStartOffsetMs + segment.EndMs;

            var absoluteStartUtc = timing.AnchorUtc.HasValue && roomActivatedAtUtc.HasValue
                ? roomActivatedAtUtc.Value.AddMilliseconds(roomRelativeStartMs)
                : (DateTime?)null;
            var absoluteEndUtc = timing.AnchorUtc.HasValue && roomActivatedAtUtc.HasValue
                ? roomActivatedAtUtc.Value.AddMilliseconds(roomRelativeEndMs)
                : (DateTime?)null;

            return new NormalizedTranscriptSegment(
                segment.ParticipantUserId,
                participantAudioTrackId,
                participantAudioFragmentId,
                segment.StartMs,
                segment.EndMs,
                roomRelativeStartMs,
                roomRelativeEndMs,
                absoluteStartUtc,
                absoluteEndUtc,
                segment.Text,
                segment.AvgLogProb,
                timing.Source);
        }

        private async Task<AiTraceTranscriptSegments> LoadAiTraceTranscriptSegmentsAsync(
            Guid meetingId,
            Guid organizationId,
            DateTime? roomActivatedAtUtc,
            CancellationToken cancellationToken)
        {
            var transcriptEventTypes = new[]
            {
                AiAssistantTraceEventTypes.TurnStarted,
                AiAssistantTraceEventTypes.SttCompleted,
                AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                AiAssistantTraceEventTypes.LlmCompleted,
                AiAssistantTraceEventTypes.TtsCompleted
            };

            var events = await _dbContext.AiAssistantTraceEvents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.MeetingId == meetingId
                            && x.OrganizationId == organizationId
                            && transcriptEventTypes.Contains(x.EventType))
                .OrderBy(x => x.OccurredAtUtc)
                .ThenBy(x => x.SessionId)
                .ThenBy(x => x.TurnId)
                .ThenBy(x => x.Sequence)
                .ThenBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new AiTraceTranscriptEvent(
                    x.Id,
                    x.EventType,
                    x.SessionId,
                    x.TurnId,
                    x.Sequence,
                    x.OccurredAtUtc,
                    x.ParticipantIdentity,
                    x.Text ?? string.Empty,
                    x.DurationMs,
                    x.AudioDurationMs,
                    x.CreatedAtUtc))
                .ToListAsync(cancellationToken);

            var turnParticipants = events
                .Where(x => EventTypeEquals(x.EventType, AiAssistantTraceEventTypes.TurnStarted))
                .Select(x => new
                {
                    Key = new TraceTurnKey(x.SessionId, x.TurnId),
                    ParticipantUserId = ParseParticipantUserId(x.ParticipantIdentity)
                })
                .Where(x => x.ParticipantUserId.HasValue)
                .GroupBy(x => x.Key)
                .ToDictionary(
                    x => x.Key,
                    x => x.Select(e => e.ParticipantUserId!.Value).First());

            var participantIds = turnParticipants.Values.Distinct().ToList();
            var participantDisplayNames = new Dictionary<Guid, string>();
            if (participantIds.Count > 0)
            {
                var participantProfiles = await _dbContext.MeetingParticipants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(x => x.MeetingId == meetingId
                                && x.OrganizationId == organizationId
                                && participantIds.Contains(x.UserId))
                    .Select(x => new
                    {
                        x.UserId,
                        x.User.DisplayName,
                        x.User.UserName
                    })
                    .ToListAsync(cancellationToken);

                participantDisplayNames = participantProfiles.ToDictionary(
                    x => x.UserId,
                    x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.UserName ?? x.UserId.ToString() : x.DisplayName);
            }

            var participantSegments = events
                .Where(x => EventTypeEquals(x.EventType, AiAssistantTraceEventTypes.SttCompleted))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .Where(x => turnParticipants.ContainsKey(new TraceTurnKey(x.SessionId, x.TurnId)))
                .Select(x =>
                {
                    var participantUserId = turnParticipants[new TraceTurnKey(x.SessionId, x.TurnId)];
                    return CreateTraceOutputSegment(
                        x,
                        ParticipantSpeakerRole,
                        participantUserId,
                        participantDisplayNames.TryGetValue(participantUserId, out var displayName)
                            ? displayName
                            : participantUserId.ToString(),
                        TimestampOffsetSources.SttCompleted,
                        SortPriority: 0,
                        roomActivatedAtUtc,
                        x.Text.Trim());
                })
                .OrderBy(x => x.RoomRelativeStartMs)
                .ThenBy(x => x.RoomRelativeEndMs)
                .ThenBy(x => x.TraceEventId, StringComparer.Ordinal)
                .ToList();

            var assistantSegments = events
                .Where(x => IsAssistantTranscriptTraceEvent(x.EventType))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .GroupBy(x => new TraceTurnKey(x.SessionId, x.TurnId))
                .SelectMany(x => CreateAssistantOutputSegments(x, roomActivatedAtUtc))
                .OrderBy(x => x.RoomRelativeStartMs)
                .ThenBy(x => x.RoomRelativeEndMs)
                .ThenBy(x => x.TraceEventId, StringComparer.Ordinal)
                .ToList();

            return new AiTraceTranscriptSegments(participantSegments, assistantSegments);
        }

        private static IReadOnlyList<TranscriptOutputSegment> CreateAssistantOutputSegments(
            IEnumerable<AiTraceTranscriptEvent> traceEvents,
            DateTime? roomActivatedAtUtc)
        {
            var orderedEvents = traceEvents
                .OrderBy(x => x.OccurredAtUtc)
                .ThenBy(x => x.Sequence)
                .ThenBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .ToList();

            var assistantSpeechEvents = orderedEvents
                .Where(x => EventTypeEquals(x.EventType, AiAssistantTraceEventTypes.AssistantSpeechCompleted))
                .GroupBy(x => new { x.SessionId, x.TurnId, x.Sequence })
                .Select(x => x.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).First())
                .Select(x => CreateTraceOutputSegment(
                    x,
                    AssistantSpeakerRole,
                    ParticipantUserId: null,
                    AssistantDisplayName,
                    TimestampOffsetSources.AssistantSpeechCompleted,
                    SortPriority: 1,
                    roomActivatedAtUtc,
                    x.Text.Trim()))
                .ToList();

            if (assistantSpeechEvents.Count > 0)
            {
                return assistantSpeechEvents;
            }

            var llmEvent = orderedEvents
                .FirstOrDefault(x => EventTypeEquals(x.EventType, AiAssistantTraceEventTypes.LlmCompleted));

            if (llmEvent is not null)
            {
                return [CreateTraceOutputSegment(
                    llmEvent,
                    AssistantSpeakerRole,
                    ParticipantUserId: null,
                    AssistantDisplayName,
                    TimestampOffsetSources.LlmCompleted,
                    SortPriority: 1,
                    roomActivatedAtUtc,
                    llmEvent.Text.Trim())];
            }

            var ttsEvents = orderedEvents
                .Where(x => EventTypeEquals(x.EventType, AiAssistantTraceEventTypes.TtsCompleted))
                .ToList();

            if (ttsEvents.Count == 0)
            {
                return [];
            }

            var firstTts = ttsEvents.First();
            var combinedText = string.Join(
                " ",
                ttsEvents.Select(x => x.Text.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)));
            var combinedDurationMs = ttsEvents.Sum(x => Math.Max(0, x.AudioDurationMs ?? x.DurationMs ?? 0));

            return [CreateTraceOutputSegment(
                firstTts with { DurationMs = combinedDurationMs, AudioDurationMs = null },
                AssistantSpeakerRole,
                ParticipantUserId: null,
                AssistantDisplayName,
                TimestampOffsetSources.TtsCompleted,
                SortPriority: 1,
                roomActivatedAtUtc,
                combinedText)];
        }

        private static TranscriptOutputSegment CreateParticipantOutputSegment(
            NormalizedTranscriptSegment segment,
            IReadOnlyDictionary<Guid, string> displayNames)
        {
            var displayName = displayNames.TryGetValue(segment.ParticipantUserId, out var resolved)
                ? resolved
                : segment.ParticipantUserId.ToString();

            return new TranscriptOutputSegment(
                ParticipantSpeakerRole,
                segment.ParticipantUserId,
                segment.ParticipantAudioTrackId,
                segment.ParticipantAudioFragmentId,
                segment.TrackRelativeStartMs,
                segment.TrackRelativeEndMs,
                segment.RoomRelativeStartMs,
                segment.RoomRelativeEndMs,
                segment.AbsoluteStartUtc,
                segment.AbsoluteEndUtc,
                segment.RoomRelativeStartMs,
                segment.RoomRelativeEndMs,
                segment.Text,
                segment.AvgLogProb,
                segment.TimestampOffsetSource,
                displayName,
                EgressAudioSource,
                TraceEventId: null,
                SessionId: null,
                TurnId: null,
                SortPriority: 0);
        }

        private static TranscriptOutputSegment CreateTraceOutputSegment(
            AiTraceTranscriptEvent traceEvent,
            string speakerRole,
            Guid? ParticipantUserId,
            string speakerDisplayName,
            string timestampOffsetSource,
            int SortPriority,
            DateTime? roomActivatedAtUtc,
            string text)
        {
            var occurredAtUtc = EnsureUtc(traceEvent.OccurredAtUtc);
            var roomRelativeStartMs = roomActivatedAtUtc.HasValue
                ? Math.Max(0, (long)Math.Round((occurredAtUtc - roomActivatedAtUtc.Value).TotalMilliseconds))
                : 0;
            var durationMs = Math.Max(0, traceEvent.AudioDurationMs ?? traceEvent.DurationMs ?? 0);
            var roomRelativeEndMs = roomRelativeStartMs + durationMs;
            var absoluteStartUtc = roomActivatedAtUtc.HasValue
                ? roomActivatedAtUtc.Value.AddMilliseconds(roomRelativeStartMs)
                : occurredAtUtc;
            var absoluteEndUtc = durationMs > 0
                ? absoluteStartUtc.AddMilliseconds(durationMs)
                : absoluteStartUtc;

            return new TranscriptOutputSegment(
                speakerRole,
                ParticipantUserId,
                ParticipantAudioTrackId: null,
                ParticipantAudioFragmentId: null,
                TrackRelativeStartMs: 0,
                TrackRelativeEndMs: durationMs,
                RoomRelativeStartMs: roomRelativeStartMs,
                RoomRelativeEndMs: roomRelativeEndMs,
                AbsoluteStartUtc: absoluteStartUtc,
                AbsoluteEndUtc: absoluteEndUtc,
                StartMs: roomRelativeStartMs,
                EndMs: roomRelativeEndMs,
                Text: text,
                AvgLogProb: null,
                TimestampOffsetSource: timestampOffsetSource,
                SpeakerDisplayName: speakerDisplayName,
                Source: AiAssistantTraceSource,
                TraceEventId: traceEvent.Id.ToString("D"),
                SessionId: traceEvent.SessionId,
                TurnId: traceEvent.TurnId,
                SortPriority);
        }

        private static bool IsAssistantTranscriptTraceEvent(string eventType)
            => EventTypeEquals(eventType, AiAssistantTraceEventTypes.AssistantSpeechCompleted)
               || EventTypeEquals(eventType, AiAssistantTraceEventTypes.LlmCompleted)
               || EventTypeEquals(eventType, AiAssistantTraceEventTypes.TtsCompleted);

        private static bool EventTypeEquals(string? actual, string expected)
            => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        private static Guid? ParseParticipantUserId(string? participantIdentity)
        {
            if (string.IsNullOrWhiteSpace(participantIdentity))
            {
                return null;
            }

            const string userPrefix = "user:";
            var value = participantIdentity.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase)
                ? participantIdentity[userPrefix.Length..]
                : participantIdentity;

            return Guid.TryParse(value, out var userId) ? userId : null;
        }

        private static DateTime EnsureUtc(DateTime value)
            => value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

        private static TrackTiming ResolveTrackTiming(
            Guid participantUserId,
            string trackSid,
            string storageObjectKey,
            DateTime? roomActivatedAtUtc,
            DateTime? fragmentTrackPublishedAtUtc,
            DateTime? fragmentEgressStartedAtUtc,
            DateTime? fragmentStorageAvailableAtUtc,
            IReadOnlyList<TranscriptTimingEvent> timingEvents)
        {
            if (!roomActivatedAtUtc.HasValue)
            {
                return new TrackTiming(0, null, TimestampOffsetSources.LegacyUnknown);
            }

            if (fragmentTrackPublishedAtUtc.HasValue)
            {
                return CreateTiming(roomActivatedAtUtc.Value, fragmentTrackPublishedAtUtc.Value, TimestampOffsetSources.FragmentTrackPublished);
            }

            var participantEvents = timingEvents
                .Where(x => x.ParticipantUserId == participantUserId)
                .ToList();

            trackSid = string.IsNullOrWhiteSpace(trackSid) ? TryExtractTrackSid(storageObjectKey) ?? string.Empty : trackSid;
            TranscriptTimingEvent? trackPublished = null;

            if (!string.IsNullOrWhiteSpace(trackSid))
            {
                trackPublished = participantEvents
                    .Where(x => x.EventType == SessionEventType.TrackPublished)
                    .Where(x => EventMatchesTrackSid(x, trackSid))
                    .OrderByDescending(x => x.OccurredAtUtc)
                    .FirstOrDefault();
            }

            trackPublished ??= participantEvents
                .Where(x => x.EventType == SessionEventType.TrackPublished)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefault();

            if (trackPublished is not null)
            {
                return CreateTiming(roomActivatedAtUtc.Value, trackPublished.OccurredAtUtc, TimestampOffsetSources.TrackPublished);
            }

            if (fragmentEgressStartedAtUtc.HasValue)
            {
                return CreateTiming(roomActivatedAtUtc.Value, fragmentEgressStartedAtUtc.Value, TimestampOffsetSources.FragmentEgressStarted);
            }

            var participantJoined = participantEvents
                .Where(x => x.EventType == SessionEventType.ParticipantJoined)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefault();

            return participantJoined is not null
                ? CreateTiming(roomActivatedAtUtc.Value, participantJoined.OccurredAtUtc, TimestampOffsetSources.ParticipantJoined)
                : fragmentStorageAvailableAtUtc.HasValue
                    ? CreateTiming(roomActivatedAtUtc.Value, fragmentStorageAvailableAtUtc.Value, TimestampOffsetSources.FragmentStorageAvailable)
                : new TrackTiming(0, null, TimestampOffsetSources.LegacyUnknown);
        }

        private static string TruncateFailureMessage(string? message)
        {
            const int maxLength = 2000;
            if (string.IsNullOrWhiteSpace(message))
            {
                return "STT transcription failed.";
            }

            return message.Length <= maxLength ? message : message[..maxLength];
        }

        private static TrackTiming CreateTiming(DateTime roomActivatedAtUtc, DateTime anchorUtc, string source)
        {
            // Webhooks may arrive with coarse second precision or slightly out of order. Do not let
            // that produce negative room-relative transcript offsets; legacy/ambiguous cases still
            // retain track-relative behavior via the LegacyUnknown source.
            var offsetMs = Math.Max(
                0,
                (long)Math.Round((anchorUtc - roomActivatedAtUtc).TotalMilliseconds));

            return new TrackTiming(offsetMs, anchorUtc, source);
        }

        private static string? TryExtractTrackSid(string storageObjectKey)
        {
            var match = TrackSidFromObjectKeyRegex.Match(storageObjectKey);
            return match.Success ? match.Groups["sid"].Value : null;
        }

        private static bool EventMatchesTrackSid(TranscriptTimingEvent timingEvent, string trackSid)
        {
            if (timingEvent.ExternalEventId.EndsWith($":{trackSid}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(timingEvent.PayloadJson)
                   && timingEvent.PayloadJson.Contains(trackSid, StringComparison.OrdinalIgnoreCase);
        }

        private static class TimestampOffsetSources
        {
            public const string FragmentTrackPublished = "fragment_track_published";
            public const string TrackPublished = "track_published";
            public const string FragmentEgressStarted = "fragment_egress_started";
            public const string ParticipantJoined = "participant_joined";
            public const string FragmentStorageAvailable = "fragment_storage_available";
            public const string AssistantSpeechCompleted = "assistant_speech_completed";
            public const string LlmCompleted = "llm_completed";
            public const string SttCompleted = "stt_completed";
            public const string TtsCompleted = "tts_completed";
            public const string LegacyUnknown = "legacy_unknown";
        }

        private sealed record FragmentTranscriptionInput(
            ParticipantAudioFragment Fragment,
            TrackTiming Timing);

        private sealed record FailedFragmentTranscription(Guid FragmentId, string FailureMessage);

        private sealed record TrackTiming(
            long RoomRelativeStartOffsetMs,
            DateTime? AnchorUtc,
            string Source);

        private sealed record TranscriptTimingEvent(
            SessionEventType EventType,
            Guid? ParticipantUserId,
            string ExternalEventId,
            string PayloadJson,
            DateTime OccurredAtUtc);

        private sealed record NormalizedTranscriptSegment(
            Guid ParticipantUserId,
            Guid? ParticipantAudioTrackId,
            Guid ParticipantAudioFragmentId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource)
        {
            public long StartMs => RoomRelativeStartMs;
            public long EndMs => RoomRelativeEndMs;
        }

        private sealed record AiTraceTranscriptSegments(
            IReadOnlyList<TranscriptOutputSegment> ParticipantSegments,
            IReadOnlyList<TranscriptOutputSegment> AssistantSegments);

        private sealed record TraceTurnKey(string SessionId, string TurnId);

        private sealed record AiTraceTranscriptEvent(
            Guid Id,
            string EventType,
            string SessionId,
            string TurnId,
            int Sequence,
            DateTime OccurredAtUtc,
            string? ParticipantIdentity,
            string Text,
            int? DurationMs,
            int? AudioDurationMs,
            DateTime CreatedAtUtc);

        private sealed record TranscriptOutputSegment(
            string SpeakerRole,
            Guid? ParticipantUserId,
            Guid? ParticipantAudioTrackId,
            Guid? ParticipantAudioFragmentId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            long StartMs,
            long EndMs,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource,
            string SpeakerDisplayName,
            string Source,
            string? TraceEventId,
            string? SessionId,
            string? TurnId,
            int SortPriority);

        private sealed record PersistedTranscriptSegment(
            int Version,
            string SpeakerRole,
            Guid? ParticipantUserId,
            Guid? ParticipantAudioTrackId,
            Guid? ParticipantAudioFragmentId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            long StartMs,
            long EndMs,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource,
            string SpeakerDisplayName,
            string Source,
            string? TraceEventId,
            string? SessionId,
            string? TurnId);
    }
}
