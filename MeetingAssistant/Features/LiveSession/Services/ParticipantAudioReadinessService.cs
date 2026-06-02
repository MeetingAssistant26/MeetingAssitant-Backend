using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public sealed class ParticipantAudioReadinessService(ApplicationDbContext dbContext) : IParticipantAudioReadinessService
    {
        private const string UniqueViolationSqlState = "23505";
        private const int SqliteConstraintViolationErrorCode = 19;

        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<ParticipantAudioReadyEvent?> TryCreateReadyEventAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var meetingIsCompleted = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .AnyAsync(
                    x => x.Id == meetingId
                         && x.OrganizationId == organizationId
                         && x.Status == MeetingStatus.Completed,
                    cancellationToken);

            if (!meetingIsCompleted)
            {
                meetingIsCompleted = await _dbContext.SessionEvents
                    .IgnoreQueryFilters()
                    .AnyAsync(
                        x => x.MeetingId == meetingId
                             && x.OrganizationId == organizationId
                             && x.EventType == SessionEventType.RoomFinished,
                        cancellationToken);
            }

            if (!meetingIsCompleted)
            {
                return null;
            }

            var pendingTracks = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .CountAsync(
                    x => x.MeetingId == meetingId
                         && x.OrganizationId == organizationId
                         && x.Status != ParticipantAudioTrackStatus.Available
                         && x.Status != ParticipantAudioTrackStatus.Failed,
                    cancellationToken);

            if (pendingTracks > 0)
            {
                return null;
            }

            var pendingFragments = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .CountAsync(
                    x => x.MeetingId == meetingId
                         && x.OrganizationId == organizationId
                         && x.Status != ParticipantAudioFragmentStatus.Available
                         && x.Status != ParticipantAudioFragmentStatus.Failed,
                    cancellationToken);

            if (pendingFragments > 0)
            {
                return null;
            }

            var occurredAtUtc = DateTime.UtcNow;
            _dbContext.SessionEvents.Add(new SessionEvent
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                EventType = SessionEventType.ParticipantAudioReady,
                ExternalEventId = $"participant-audio-ready:{meetingId}",
                PayloadJson = "{}",
                OccurredAtUtc = occurredAtUtc,
                ProcessedAtUtc = occurredAtUtc
            });

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _dbContext.ChangeTracker.Clear();
                return null;
            }

            return new ParticipantAudioReadyEvent(meetingId, organizationId, occurredAtUtc);
        }

        private static bool IsUniqueViolation(DbUpdateException exception)
        {
            return exception.InnerException is PostgresException { SqlState: UniqueViolationSqlState }
                   || IsSqliteUniqueViolation(exception.InnerException);
        }

        private static bool IsSqliteUniqueViolation(Exception? exception)
        {
            if (exception?.GetType().FullName != "Microsoft.Data.Sqlite.SqliteException")
            {
                return false;
            }

            var errorCode = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception) as int?;
            return errorCode == SqliteConstraintViolationErrorCode;
        }
    }
}
