using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class AiDebugTraceService(
        ApplicationDbContext dbContext,
        IOptions<AiDebugOptions> options) : IAiDebugTraceService
    {
        private const int MaxEvents = 50;
        private const int MaxTurns = 20;

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly AiDebugOptions _options = options.Value;

        public async Task<Result> IngestAsync(
            Guid organizationId,
            Guid meetingId,
            AiDebugTraceIngestRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return Result.Failure(AiDebugErrors.Disabled);
            }

            if (string.IsNullOrWhiteSpace(request.SessionId)
                || string.IsNullOrWhiteSpace(request.TurnId)
                || string.IsNullOrWhiteSpace(request.EventType))
            {
                return Result.Failure(AiDebugErrors.InvalidTrace);
            }

            var meetingExists = await _dbContext.Meetings
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (!meetingExists)
            {
                return Result.Failure(MeetingErrors.NotFound);
            }

            var persistPayloads = _options.PersistPayloads;
            var persistText = persistPayloads
                              || string.Equals(
                                  request.EventType,
                                  AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                                  StringComparison.OrdinalIgnoreCase);
            var step = request.Step;
            var entity = new AiAssistantTraceEvent
            {
                OrganizationId = organizationId,
                MeetingId = meetingId,
                SessionId = TrimRequired(request.SessionId, 128),
                TurnId = TrimRequired(request.TurnId, 128),
                Sequence = request.Sequence,
                EventType = TrimRequired(request.EventType, 64),
                OccurredAtUtc = EnsureUtc(request.OccurredAtUtc),
                ParticipantIdentity = TrimOptional(request.ParticipantIdentity, 256),
                State = TrimOptional(request.State, 128),
                StepType = TrimOptional(step?.Type, 32),
                StepProvider = TrimOptional(step?.Provider, 128),
                StepEndpoint = TrimOptional(step?.Endpoint, 512),
                StepModel = TrimOptional(step?.Model, 128),
                StepVoice = TrimOptional(step?.Voice, 128),
                DurationMs = NonNegativeOrNull(step?.DurationMs),
                PromptTokens = NonNegativeOrNull(step?.PromptTokens),
                CompletionTokens = NonNegativeOrNull(step?.CompletionTokens),
                TotalTokens = NonNegativeOrNull(step?.TotalTokens),
                CharactersCount = NonNegativeOrNull(step?.CharactersCount) ?? (persistPayloads ? null : step?.Text?.Length),
                AudioDurationMs = NonNegativeOrNull(step?.AudioDurationMs),
                RequestPayloadJson = persistPayloads ? ToJson(step?.RequestPayload) : null,
                ResponsePayloadJson = persistPayloads ? ToJson(step?.ResponsePayload) : null,
                Text = persistText ? step?.Text : null,
                ErrorMessage = request.Error?.Message,
                ErrorType = TrimOptional(request.Error?.Type, 128)
            };

            _dbContext.AiAssistantTraceEvents.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result<AiDebugTraceResponse>> GetTracesAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return Result.Success(new AiDebugTraceResponse(false, false, []));
            }

            var access = await ValidateOrganizationAdminMeetingAsync(
                organizationId,
                meetingId,
                callerUserId,
                cancellationToken);

            if (access.IsFailure)
            {
                return Result.Failure<AiDebugTraceResponse>(access.Error);
            }

            var recentEvents = await _dbContext.AiAssistantTraceEvents
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(e => e.OrganizationId == organizationId && e.MeetingId == meetingId)
                .OrderByDescending(e => e.OccurredAtUtc)
                .ThenByDescending(e => e.CreatedAtUtc)
                .ThenByDescending(e => e.Id)
                .Take(MaxEvents)
                .ToListAsync(cancellationToken);

            var turns = recentEvents
                .GroupBy(e => new { e.SessionId, e.TurnId })
                .Select(g => BuildTurn(g.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Sequence).ThenBy(e => e.CreatedAtUtc).ToList()))
                .OrderByDescending(t => t.StartedAtUtc)
                .Take(MaxTurns)
                .ToList();

            return Result.Success(new AiDebugTraceResponse(true, _options.PersistPayloads, turns));
        }

        private async Task<Result> ValidateOrganizationAdminMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken)
        {
            var access = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(m => m.Id == meetingId && m.OrganizationId == organizationId)
                .Select(m => new
                {
                    IsOrganizationAdmin = _dbContext.UserOrgMemberships
                        .IgnoreQueryFilters()
                        .Any(x => x.OrganizationId == organizationId
                                  && x.UserId == callerUserId
                                  && x.IsEnabled
                                  && x.OrgRole == OrganizationRole.Admin)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (access is null)
            {
                return Result.Failure(LiveSessionErrors.MeetingNotFound);
            }

            return access.IsOrganizationAdmin
                ? Result.Success()
                : Result.Failure(OrganizationErrors.Unauthorized);
        }

        private static AiDebugTurnResponse BuildTurn(IReadOnlyList<AiAssistantTraceEvent> events)
        {
            var startedAt = events.First().OccurredAtUtc;
            var hasError = events.Any(e => string.Equals(e.EventType, "error", StringComparison.OrdinalIgnoreCase)
                                           || !string.IsNullOrWhiteSpace(e.ErrorMessage)
                                           || !string.IsNullOrWhiteSpace(e.ErrorType));
            var hasCompleted = events.Any(e => string.Equals(e.EventType, "turn_completed", StringComparison.OrdinalIgnoreCase));
            var status = hasError ? "error" : hasCompleted ? "completed" : "active";
            DateTime? completedAt = status is "completed" or "error" ? events.Max(e => e.OccurredAtUtc) : null;
            var totalDurationMs = completedAt.HasValue
                ? (int?)Math.Max(0, (int)Math.Round((completedAt.Value - startedAt).TotalMilliseconds))
                : events.Sum(e => e.DurationMs.GetValueOrDefault()) is var sum && sum > 0 ? sum : null;

            return new AiDebugTurnResponse(
                events.First().SessionId,
                events.First().TurnId,
                events.Select(e => e.ParticipantIdentity).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                startedAt,
                completedAt,
                status,
                totalDurationMs,
                events.Select(ToResponse).ToList());
        }

        private static AiDebugTraceEventResponse ToResponse(AiAssistantTraceEvent entity)
        {
            var step = HasStep(entity)
                ? new AiDebugTraceStepDto(
                    entity.StepType,
                    entity.StepProvider,
                    entity.StepEndpoint,
                    entity.StepModel,
                    entity.StepVoice,
                    entity.DurationMs,
                    entity.PromptTokens,
                    entity.CompletionTokens,
                    entity.TotalTokens,
                    entity.CharactersCount,
                    entity.AudioDurationMs,
                    FromJson(entity.RequestPayloadJson),
                    FromJson(entity.ResponsePayloadJson),
                    entity.Text)
                : null;

            var error = !string.IsNullOrWhiteSpace(entity.ErrorMessage) || !string.IsNullOrWhiteSpace(entity.ErrorType)
                ? new AiDebugTraceErrorDto(entity.ErrorMessage, entity.ErrorType)
                : null;

            return new AiDebugTraceEventResponse(
                entity.Id,
                entity.Sequence,
                entity.EventType,
                entity.OccurredAtUtc,
                entity.State,
                step,
                error);
        }

        private static bool HasStep(AiAssistantTraceEvent entity)
            => !string.IsNullOrWhiteSpace(entity.StepType)
               || !string.IsNullOrWhiteSpace(entity.StepProvider)
               || !string.IsNullOrWhiteSpace(entity.StepEndpoint)
               || !string.IsNullOrWhiteSpace(entity.StepModel)
               || !string.IsNullOrWhiteSpace(entity.StepVoice)
               || entity.DurationMs.HasValue
               || entity.PromptTokens.HasValue
               || entity.CompletionTokens.HasValue
               || entity.TotalTokens.HasValue
               || entity.CharactersCount.HasValue
               || entity.AudioDurationMs.HasValue
               || !string.IsNullOrWhiteSpace(entity.RequestPayloadJson)
               || !string.IsNullOrWhiteSpace(entity.ResponsePayloadJson)
               || !string.IsNullOrWhiteSpace(entity.Text);

        private static string? ToJson(JsonElement? value)
            => value.HasValue ? value.Value.GetRawText() : null;

        private static JsonElement? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
        }

        private static int? NonNegativeOrNull(int? value)
            => value.HasValue ? Math.Max(0, value.Value) : null;

        private static string TrimRequired(string value, int maxLength)
            => value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

        private static string? TrimOptional(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed[..Math.Min(trimmed.Length, maxLength)];
        }
    }
}
