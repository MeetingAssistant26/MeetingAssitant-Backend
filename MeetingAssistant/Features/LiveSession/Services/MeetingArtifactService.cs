using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class MeetingArtifactService(ApplicationDbContext dbContext) : IMeetingArtifactService
    {
        private const string Available = "available";
        private const string Processing = "processing";
        private const string NotAvailable = "not_available";
        private const string NotRelevant = "not_relevant";

        private static readonly JsonSerializerOptions SegmentJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<MeetingTranscriptResponse>> GetTranscriptAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meetingStatusResult = await GetMeetingStatusAsync(organizationId, meetingId, cancellationToken);
            if (meetingStatusResult.IsFailure)
            {
                return Result.Failure<MeetingTranscriptResponse>(meetingStatusResult.Error);
            }

            var transcript = await _dbContext.MeetingTranscripts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId, cancellationToken);

            if (transcript == null)
            {
                return Result.Success(new MeetingTranscriptResponse(
                    meetingId,
                    ResolveMissingArtifactStatus(meetingStatusResult.Value),
                    null,
                    Array.Empty<MeetingTranscriptSegmentResponse>(),
                    null,
                    null));
            }

            var segments = DeserializeSegments(transcript.SegmentsJson);

            return Result.Success(new MeetingTranscriptResponse(
                meetingId,
                Available,
                transcript.FullText,
                segments,
                string.IsNullOrWhiteSpace(transcript.SttModel) ? null : transcript.SttModel,
                transcript.GeneratedAtUtc,
                transcript.CompletenessStatus.ToString(),
                transcript.CompletenessStatus != MeetingTranscriptCompletenessStatus.Complete,
                transcript.ExpectedAudioFragmentCount,
                transcript.TranscribedAudioFragmentCount,
                transcript.RetryableFailedAudioFragmentCount,
                transcript.TerminalFailedAudioFragmentCount));
        }

        public async Task<Result<MeetingSummaryResponse>> GetSummaryAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meetingStatusResult = await GetMeetingStatusAsync(organizationId, meetingId, cancellationToken);
            if (meetingStatusResult.IsFailure)
            {
                return Result.Failure<MeetingSummaryResponse>(meetingStatusResult.Error);
            }

            var summary = await _dbContext.MeetingSummaries
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId, cancellationToken);

            if (summary == null)
            {
                return Result.Success(new MeetingSummaryResponse(
                    meetingId,
                    ResolveMissingArtifactStatus(meetingStatusResult.Value),
                    null,
                    null,
                    null,
                    null,
                    null));
            }

            return Result.Success(new MeetingSummaryResponse(
                meetingId,
                Available,
                summary.SummaryText,
                string.IsNullOrWhiteSpace(summary.LlmModel) ? null : summary.LlmModel,
                summary.PromptTokens,
                summary.CompletionTokens,
                summary.GeneratedAtUtc));
        }

        public async Task<Result<PersonalizedMeetingSummaryResponse>> GetPersonalizedSummaryAsync(
            Guid organizationId,
            Guid meetingId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var meetingStatusResult = await GetMeetingStatusAsync(organizationId, meetingId, cancellationToken);
            if (meetingStatusResult.IsFailure)
            {
                return Result.Failure<PersonalizedMeetingSummaryResponse>(meetingStatusResult.Error);
            }

            var participantId = await _dbContext.MeetingParticipants
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId && x.UserId == userId)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (!participantId.HasValue)
            {
                return Result.Failure<PersonalizedMeetingSummaryResponse>(LiveSessionErrors.NotAParticipant);
            }

            var summary = await _dbContext.PersonalizedMeetingSummaries
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.OrganizationId == organizationId && x.MeetingId == meetingId && x.UserId == userId,
                    cancellationToken);

            if (summary == null)
            {
                return Result.Success(new PersonalizedMeetingSummaryResponse(
                    meetingId,
                    userId,
                    participantId,
                    ResolveMissingArtifactStatus(meetingStatusResult.Value),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }

            return Result.Success(new PersonalizedMeetingSummaryResponse(
                meetingId,
                summary.UserId,
                summary.MeetingParticipantId,
                ResolvePersonalizedSummaryStatus(summary.Status),
                summary.SummaryText,
                string.IsNullOrWhiteSpace(summary.LlmModel) ? null : summary.LlmModel,
                summary.PromptTokens,
                summary.CompletionTokens,
                summary.GeneratedAtUtc,
                summary.TargetDisplayName,
                summary.PromptName,
                summary.PromptVersion));
        }

        private async Task<Result<MeetingStatus>> GetMeetingStatusAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var status = await _dbContext.Meetings
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Id == meetingId)
                .Select(x => (MeetingStatus?)x.Status)
                .FirstOrDefaultAsync(cancellationToken);

            return status.HasValue
                ? Result.Success(status.Value)
                : Result.Failure<MeetingStatus>(LiveSessionErrors.MeetingNotFound);
        }

        private static string ResolveMissingArtifactStatus(MeetingStatus meetingStatus)
            => meetingStatus is MeetingStatus.InProgress or MeetingStatus.Scheduled
                ? Processing
                : NotAvailable;

        private static string ResolvePersonalizedSummaryStatus(PersonalizedMeetingSummaryStatus status)
            => status == PersonalizedMeetingSummaryStatus.Skipped ? NotRelevant : Available;

        private static IReadOnlyList<MeetingTranscriptSegmentResponse> DeserializeSegments(string segmentsJson)
        {
            if (string.IsNullOrWhiteSpace(segmentsJson))
            {
                return Array.Empty<MeetingTranscriptSegmentResponse>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<MeetingTranscriptSegmentResponse>>(segmentsJson, SegmentJsonOptions)
                    ?? (IReadOnlyList<MeetingTranscriptSegmentResponse>)Array.Empty<MeetingTranscriptSegmentResponse>();
            }
            catch (JsonException)
            {
                return Array.Empty<MeetingTranscriptSegmentResponse>();
            }
        }
    }
}
