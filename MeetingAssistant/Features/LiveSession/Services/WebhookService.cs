using Hangfire;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Hubs;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class WebhookService(
        ApplicationDbContext dbContext,
        IBackgroundJobClient backgroundJobClient,
        ILiveSessionNotifier liveSessionNotifier,
        ILogger<WebhookService> logger) : IWebhookService
    {
        private const string UniqueViolationSqlState = "23505";

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly ILiveSessionNotifier _liveSessionNotifier = liveSessionNotifier;
        private readonly ILogger<WebhookService> _logger = logger;

        public async Task<Result> ProcessAsync(
            WebhookEvent webhookEvent,
            string rawPayload,
            CancellationToken cancellationToken = default)
        {
            var eventType = MapEventType(webhookEvent.Event);

            if (!TryResolveMeetingId(webhookEvent, out var meetingId))
            {
                _logger.LogWarning(
                    "LiveKit webhook ignored because room name was not resolvable. Event={Event} EventId={EventId}",
                    webhookEvent.Event,
                    webhookEvent.Id);
                return Result.Success();
            }

            var meeting = await _dbContext.Meetings
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
            {
                _logger.LogWarning(
                    "LiveKit webhook ignored because no meeting matched room for Event={Event} EventId={EventId}",
                    webhookEvent.Event,
                    webhookEvent.Id);
                return Result.Success();
            }

            var externalEventId = ResolveExternalEventId(webhookEvent, eventType, meetingId);
            var alreadyProcessed = await _dbContext.SessionEvents
                .AnyAsync(x => x.ExternalEventId == externalEventId, cancellationToken);

            if (alreadyProcessed)
            {
                return Result.Success();
            }

            var occurredAtUtc = ResolveOccurredAtUtc(webhookEvent.CreatedAt);
            var participantUserId = await ResolveParticipantUserIdAsync(
                meetingId,
                webhookEvent.Participant?.Identity,
                cancellationToken);

            var sessionEvent = new SessionEvent
            {
                MeetingId = meeting.Id,
                OrganizationId = meeting.OrganizationId,
                ExternalEventId = externalEventId,
                EventType = eventType,
                ParticipantUserId = participantUserId,
                PayloadJson = rawPayload,
                OccurredAtUtc = occurredAtUtc,
                ProcessedAtUtc = DateTime.UtcNow
            };

            _dbContext.SessionEvents.Add(sessionEvent);

            var notifications = new List<Func<CancellationToken, Task>>();
            string? sourceCloudUrl = null;
            var enqueueDownload = false;

            switch (eventType)
            {
                case SessionEventType.RoomStarted:
                    if (meeting.Status == Features.Meetings.Models.MeetingStatus.Scheduled)
                    {
                        meeting.Status = Features.Meetings.Models.MeetingStatus.InProgress;
                        meeting.RaiseDomainEvent(new SessionStartedEvent(meeting.Id, meeting.OrganizationId, occurredAtUtc));
                        notifications.Add(ct => _liveSessionNotifier.NotifySessionStartedAsync(meeting.OrganizationId, meeting.Id, occurredAtUtc, ct));
                    }
                    break;

                case SessionEventType.RoomFinished:
                    if (meeting.Status != Features.Meetings.Models.MeetingStatus.Completed)
                    {
                        meeting.Status = Features.Meetings.Models.MeetingStatus.Completed;
                        meeting.RaiseDomainEvent(new SessionEndedEvent(meeting.Id, meeting.OrganizationId, occurredAtUtc));
                        notifications.Add(ct => _liveSessionNotifier.NotifySessionEndedAsync(meeting.OrganizationId, meeting.Id, occurredAtUtc, ct));
                    }
                    break;

                case SessionEventType.ParticipantJoined:
                    if (participantUserId.HasValue)
                    {
                        var joinedUserId = participantUserId.Value;
                        notifications.Add(ct => _liveSessionNotifier.NotifyParticipantJoinedAsync(
                            meeting.OrganizationId,
                            meeting.Id,
                            joinedUserId,
                            occurredAtUtc,
                            ct));
                    }
                    break;

                case SessionEventType.ParticipantLeft:
                    if (participantUserId.HasValue)
                    {
                        var leftUserId = participantUserId.Value;
                        notifications.Add(ct => _liveSessionNotifier.NotifyParticipantLeftAsync(
                            meeting.OrganizationId,
                            meeting.Id,
                            leftUserId,
                            occurredAtUtc,
                            ct));
                    }
                    break;

                case SessionEventType.RecordingStarted:
                    await UpsertRecordingAsync(meeting.Id, meeting.OrganizationId, ParticipantAudioTrackStatus.Pending, null, cancellationToken);
                    break;

                case SessionEventType.EgressEnded:
                    sourceCloudUrl = ResolveSourceCloudUrl(webhookEvent);
                    var isSuccess = webhookEvent.EgressInfo?.Status == EgressStatus.EgressComplete
                        && !string.IsNullOrWhiteSpace(sourceCloudUrl);

                    if (isSuccess)
                    {
                        await UpsertRecordingAsync(meeting.Id, meeting.OrganizationId, ParticipantAudioTrackStatus.Pending, null, cancellationToken);
                        enqueueDownload = true;
                    }
                    else
                    {
                        await UpsertRecordingAsync(meeting.Id, meeting.OrganizationId, ParticipantAudioTrackStatus.Failed, null, cancellationToken);
                    }
                    break;

                case SessionEventType.Unknown:
                default:
                    break;
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return Result.Success();
            }

            if (enqueueDownload && !string.IsNullOrWhiteSpace(sourceCloudUrl))
            {
                _backgroundJobClient.Enqueue<DownloadRecordingJob>(
                    job => job.RunAsync(meeting.Id, sourceCloudUrl, CancellationToken.None));
            }

            try
            {
                await Task.WhenAll(notifications.Select(notify => notify(cancellationToken)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Live session notification dispatch failed for MeetingId={MeetingId} EventType={EventType}",
                    meeting.Id,
                    eventType);
            }

            return Result.Success();
        }

        private async Task UpsertRecordingAsync(
            Guid meetingId,
            Guid organizationId,
            ParticipantAudioTrackStatus status,
            string? filePath,
            CancellationToken cancellationToken)
        {
            var recording = await _dbContext.ParticipantAudioTracks
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId, cancellationToken);

            if (recording == null)
            {
                recording = new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId,
                    ParticipantUserId = Guid.Empty,
                    Status = status,
                    StorageObjectKey = filePath
                };

                _dbContext.ParticipantAudioTracks.Add(recording);
                return;
            }

            recording.Status = status;
            recording.StorageObjectKey = filePath;
        }

        private static string? ResolveSourceCloudUrl(WebhookEvent webhookEvent)
        {
            var fileResultLocation = webhookEvent.EgressInfo?.FileResults
                .FirstOrDefault()?.Location;

            return string.IsNullOrWhiteSpace(fileResultLocation)
                ? null
                : fileResultLocation;
        }

        private static SessionEventType MapEventType(string? eventName)
        {
            return eventName?.ToLowerInvariant() switch
            {
                "room_started" => SessionEventType.RoomStarted,
                "room_finished" => SessionEventType.RoomFinished,
                "participant_joined" => SessionEventType.ParticipantJoined,
                "participant_left" => SessionEventType.ParticipantLeft,
                "recording_started" => SessionEventType.RecordingStarted,
                "egress_ended" => SessionEventType.EgressEnded,
                _ => SessionEventType.Unknown
            };
        }

        private static DateTime ResolveOccurredAtUtc(long createdAtUnixSeconds)
        {
            return createdAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(createdAtUnixSeconds).UtcDateTime
                : DateTime.UtcNow;
        }

        private static string ResolveExternalEventId(WebhookEvent webhookEvent, SessionEventType eventType, Guid meetingId)
        {
            if (!string.IsNullOrWhiteSpace(webhookEvent.Id))
            {
                return webhookEvent.Id;
            }

            return $"{eventType}:{meetingId}:{webhookEvent.CreatedAt}";
        }

        private static bool TryResolveMeetingId(WebhookEvent webhookEvent, out Guid meetingId)
        {
            meetingId = Guid.Empty;

            var roomName = webhookEvent.Room?.Name;
            if (!string.IsNullOrWhiteSpace(roomName)
                && TryParseMeetingId(roomName, out meetingId))
            {
                return true;
            }

            roomName = webhookEvent.EgressInfo?.RoomName;
            if (!string.IsNullOrWhiteSpace(roomName)
                && TryParseMeetingId(roomName, out meetingId))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseMeetingId(string roomName, out Guid meetingId)
        {
            meetingId = Guid.Empty;

            const string prefix = "mtg:";
            if (!roomName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var idPart = roomName[prefix.Length..];
            return Guid.TryParse(idPart, out meetingId);
        }

        private async Task<Guid?> ResolveParticipantUserIdAsync(
            Guid meetingId,
            string? participantIdentity,
            CancellationToken cancellationToken)
        {
            if (!TryParseParticipantIdentity(participantIdentity, out var userId))
            {
                return null;
            }

            var exists = await _dbContext.MeetingParticipants
                .AnyAsync(x => x.MeetingId == meetingId && x.UserId == userId, cancellationToken);

            return exists ? userId : null;
        }

        private static bool TryParseParticipantIdentity(string? identity, out Guid userId)
        {
            userId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            const string prefix = "user:";
            if (!identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Guid.TryParse(identity[prefix.Length..], out userId);
        }

        private static bool IsUniqueViolation(DbUpdateException exception)
        {
            return exception.InnerException is PostgresException postgresException
                && postgresException.SqlState == UniqueViolationSqlState;
        }
    }
}
