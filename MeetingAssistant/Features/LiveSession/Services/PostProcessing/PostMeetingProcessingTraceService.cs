using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing
{
    public class PostMeetingProcessingTraceService(
        ApplicationDbContext dbContext,
        IOptions<AiDebugOptions> options) : IPostMeetingProcessingTraceService
    {
        private const int MaxRuns = 10;
        private const int MaxEvents = 250;

        private static readonly Regex BearerTokenRegex = new(
            @"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex JwtRegex = new(
            @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ProviderKeyRegex = new(
            @"\b(?:sk|pk|rk)-[A-Za-z0-9_-]{12,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UrlRegex = new(
            @"(?i)\bhttps?://[^\s)>'""\]]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ConnectionStringSecretRegex = new(
            @"(?i)\b(Password|Pwd|User\s*ID|Username|AccountKey|SharedAccessKey|Secret|ApiKey|AccessKey|Token)\s*=\s*([^;\s]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex QueryStringSecretRegex = new(
            @"(?i)([?&](?:access_token|refresh_token|id_token|token|sig|signature|x-amz-signature|x-amz-security-token|x-amz-credential|api[_-]?key|key|secret|password|code)=)[^&\s]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex JsonSecretRegex = new(
            @"(?i)(""(?:accessToken|refreshToken|idToken|token|apiKey|secret|password|connectionString|authorization)""\s*:\s*"")[^""]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly AiDebugOptions _options = options.Value;

        public async Task<Result<PostMeetingProcessingTraceResponse>> GetTracesAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return Result.Success(new PostMeetingProcessingTraceResponse(false, [], [], []));
            }

            var meetingExists = await _dbContext.Meetings
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (!meetingExists)
            {
                return Result.Failure<PostMeetingProcessingTraceResponse>(LiveSessionErrors.MeetingNotFound);
            }

            var runs = await _dbContext.PostMeetingProcessingRuns
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(MaxRuns)
                .ToListAsync(cancellationToken);

            if (runs.Count == 0)
            {
                return Result.Success(new PostMeetingProcessingTraceResponse(true, [], [], []));
            }

            var runIds = runs.Select(x => x.Id).ToArray();

            var steps = await _dbContext.PostMeetingProcessingSteps
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && runIds.Contains(x.RunId))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.StepType)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var events = await _dbContext.PostMeetingProcessingEvents
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && runIds.Contains(x.RunId))
                .OrderByDescending(x => x.OccurredAtUtc)
                .ThenByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(MaxEvents)
                .ToListAsync(cancellationToken);

            return Result.Success(new PostMeetingProcessingTraceResponse(
                true,
                runs.Select(run => ToRunResponse(run, steps.Where(step => step.RunId == run.Id).ToList())).ToList(),
                steps.Select(ToStepResponse).ToList(),
                events.Select(ToEventResponse).ToList()));
        }

        private static PostMeetingProcessingRunTraceResponse ToRunResponse(
            PostMeetingProcessingRun run,
            IReadOnlyCollection<PostMeetingProcessingStep> steps)
        {
            var status = DeriveSafeRunStatus(run, steps);
            return new(
                run.Id,
                run.MeetingId,
                run.PipelineGenerationId,
                status.ToString(),
                run.AttemptCount,
                run.StartedAtUtc,
                run.CompletedAtUtc ?? (status == PostMeetingProcessingStatus.Completed ? LatestTerminalStepTime(steps) : null),
                run.FailedAtUtc ?? (status == PostMeetingProcessingStatus.Failed ? LatestTerminalStepTime(steps) : null),
                run.RelatedHangfireJobId,
                ToError(run.ErrorCode, run.ErrorMessage),
                run.CreatedAtUtc,
                run.UpdatedAtUtc);
        }

        private static PostMeetingProcessingStepTraceResponse ToStepResponse(PostMeetingProcessingStep step)
            => new(
                step.Id,
                step.RunId,
                step.MeetingId,
                step.StepType.ToString(),
                step.Status.ToString(),
                step.AttemptCount,
                step.LastAttemptAtUtc,
                step.StartedAtUtc,
                step.CompletedAtUtc,
                step.FailedAtUtc,
                step.RelatedHangfireJobId,
                ToArtifact(step.ArtifactType, step.ArtifactId, step.ArtifactIdsJson),
                ToError(step.ErrorCode, step.ErrorMessage),
                step.CreatedAtUtc,
                step.UpdatedAtUtc);

        private static PostMeetingProcessingEventTraceResponse ToEventResponse(PostMeetingProcessingEvent processingEvent)
            => new(
                processingEvent.Id,
                processingEvent.RunId,
                processingEvent.StepId,
                processingEvent.MeetingId,
                processingEvent.StepType?.ToString(),
                processingEvent.EventType.ToString(),
                processingEvent.Status?.ToString(),
                processingEvent.OccurredAtUtc,
                processingEvent.RelatedHangfireJobId,
                processingEvent.Message,
                ToArtifact(processingEvent.ArtifactType, processingEvent.ArtifactId, processingEvent.ArtifactIdsJson),
                ToError(processingEvent.ErrorCode, processingEvent.ErrorMessage),
                processingEvent.CreatedAtUtc,
                processingEvent.UpdatedAtUtc);

        private static PostMeetingProcessingArtifactDto? ToArtifact(
            string? artifactType,
            Guid? artifactId,
            string? artifactIdsJson)
        {
            var artifactIds = ParseArtifactIds(artifactIdsJson);
            if (string.IsNullOrWhiteSpace(artifactType) && !artifactId.HasValue && artifactIds.Count == 0)
            {
                return null;
            }

            return new PostMeetingProcessingArtifactDto(artifactType, artifactId, artifactIds);
        }

        private static PostMeetingProcessingErrorDto? ToError(string? code, string? message)
        {
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return new PostMeetingProcessingErrorDto(code, RedactSensitiveErrorMessage(message));
        }

        private static PostMeetingProcessingStatus DeriveSafeRunStatus(
            PostMeetingProcessingRun run,
            IReadOnlyCollection<PostMeetingProcessingStep> steps)
        {
            if (run.Status is PostMeetingProcessingStatus.Completed or PostMeetingProcessingStatus.Failed or PostMeetingProcessingStatus.Skipped)
            {
                return run.Status;
            }

            if (steps.Count == 0 || steps.Any(step => step.Status is PostMeetingProcessingStatus.InProgress or PostMeetingProcessingStatus.Pending))
            {
                return run.Status;
            }

            if (steps.All(IsTerminalStatus))
            {
                return steps.Any(step => step.Status == PostMeetingProcessingStatus.Failed)
                    ? PostMeetingProcessingStatus.Failed
                    : PostMeetingProcessingStatus.Completed;
            }

            return run.Status;
        }

        private static bool IsTerminalStatus(PostMeetingProcessingStep step)
            => step.Status is PostMeetingProcessingStatus.Completed or PostMeetingProcessingStatus.Failed or PostMeetingProcessingStatus.Skipped;

        private static DateTime? LatestTerminalStepTime(IReadOnlyCollection<PostMeetingProcessingStep> steps)
            => steps.Count == 0
                ? null
                : steps.Max(step => step.CompletedAtUtc ?? step.FailedAtUtc ?? step.UpdatedAtUtc);

        private static string? RedactSensitiveErrorMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            var redacted = message;
            redacted = BearerTokenRegex.Replace(redacted, "$1[redacted]");
            redacted = JwtRegex.Replace(redacted, "[redacted-jwt]");
            redacted = ProviderKeyRegex.Replace(redacted, "[redacted-key]");
            redacted = UrlRegex.Replace(redacted, RedactUrl);
            redacted = QueryStringSecretRegex.Replace(redacted, "$1[redacted]");
            redacted = ConnectionStringSecretRegex.Replace(redacted, "$1=[redacted]");
            redacted = JsonSecretRegex.Replace(redacted, "$1[redacted]");

            return redacted;
        }

        private static string RedactUrl(Match match)
        {
            var value = match.Value;
            var queryStart = value.IndexOf('?');
            if (queryStart < 0)
            {
                return value;
            }

            return string.Concat(value.AsSpan(0, queryStart), "?[redacted]");
        }

        private static IReadOnlyList<Guid> ParseArtifactIds(string? artifactIdsJson)
        {
            if (string.IsNullOrWhiteSpace(artifactIdsJson))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<Guid>>(artifactIdsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
