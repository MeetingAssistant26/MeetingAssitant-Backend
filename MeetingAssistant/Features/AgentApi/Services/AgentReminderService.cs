using Mapster;
using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Tasks.Models.Entities;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Features.Tasks.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public class AgentReminderService(ApplicationDbContext dbContext) : IAgentReminderService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<AgentReminderResponse>> CreateReminderAsync(
            CreateAgentReminderRequest request,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.TryParse<ReminderScope>(request.Scope, true, out var scope))
            {
                return Result.Failure<AgentReminderResponse>(AgentApiErrors.InvalidReminderScope);
            }

            var meetingExists = await _dbContext.Meetings
                .AsNoTracking()
                .AnyAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (!meetingExists)
            {
                return Result.Failure<AgentReminderResponse>(MeetingErrors.NotFound);
            }

            var reminder = new Reminder
            {
                OrganizationId = organizationId,
                Text = request.Text,
                Scope = scope,
                Channel = ReminderChannel.Agent,
                CreatedByUserId = request.CreatedByUserId,
                TargetUserId = scope == ReminderScope.Personal ? request.TargetUserId : null,
                MeetingId = meetingId,
                ReminderAtUtc = request.ReminderAtUtc,
                Status = ReminderStatus.Active
            };

            reminder.RaiseDomainEvent(new ReminderCreatedEvent(organizationId, reminder.Id));

            _dbContext.Reminders.Add(reminder);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(reminder.Adapt<AgentReminderResponse>());
        }

        public async Task<Result<IReadOnlyList<AgentReminderResponse>>> ListPublicMeetingRemindersAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var meeting = await _dbContext.Meetings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == meetingId && m.OrganizationId == organizationId, cancellationToken);

            if (meeting == null)
            {
                return Result.Failure<IReadOnlyList<AgentReminderResponse>>(MeetingErrors.NotFound);
            }

            var reminders = await _dbContext.Reminders
                .AsNoTracking()
                .Where(r => r.OrganizationId == organizationId)
                .Where(r => r.MeetingId == meetingId)
                .Where(r => r.Scope == ReminderScope.Public)
                .Where(r => r.Status == ReminderStatus.Active)
                .Where(r => r.ReminderAtUtc <= meeting.ScheduledStartUtc)
                .OrderBy(r => r.ReminderAtUtc)
                .ThenBy(r => r.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<AgentReminderResponse>>(reminders.Adapt<List<AgentReminderResponse>>());
        }

        public async Task<Result> MarkReminderDeliveredAsync(
            Guid reminderId,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var reminder = await _dbContext.Reminders
                .FirstOrDefaultAsync(
                    r => r.Id == reminderId && r.OrganizationId == organizationId && r.MeetingId == meetingId,
                    cancellationToken);

            if (reminder == null)
            {
                return Result.Failure(ReminderErrors.NotFound);
            }

            if (reminder.Scope != ReminderScope.Public)
            {
                return Result.Failure(AgentApiErrors.PublicReminderRequired);
            }

            if (reminder.Status == ReminderStatus.Delivered)
            {
                return Result.Success();
            }

            if (reminder.Status == ReminderStatus.Cancelled)
            {
                return Result.Failure(ReminderErrors.InvalidStatus);
            }

            reminder.Status = ReminderStatus.Delivered;
            reminder.DeliveredAtUtc = DateTime.UtcNow;
            reminder.RaiseDomainEvent(new ReminderDeliveredEvent(organizationId, reminder.Id));

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result> CancelReminderAsync(
            Guid reminderId,
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var reminder = await _dbContext.Reminders
                .FirstOrDefaultAsync(
                    r => r.Id == reminderId && r.OrganizationId == organizationId && r.MeetingId == meetingId,
                    cancellationToken);

            if (reminder == null)
            {
                return Result.Failure(ReminderErrors.NotFound);
            }

            if (reminder.Channel != ReminderChannel.Agent)
            {
                return Result.Failure(AgentApiErrors.AgentOwnedReminderRequired);
            }

            if (reminder.Status == ReminderStatus.Cancelled)
            {
                return Result.Success();
            }

            reminder.Status = ReminderStatus.Cancelled;
            reminder.RaiseDomainEvent(new ReminderCancelledEvent(organizationId, reminder.Id));

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
