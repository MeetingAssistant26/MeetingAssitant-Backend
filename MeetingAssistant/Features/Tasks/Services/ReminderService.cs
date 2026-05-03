using Mapster;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Tasks.Contracts.Requests;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using MeetingAssistant.Features.Tasks.Models.Entities;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Features.Tasks.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Tasks.Services
{
    public class ReminderService(
        ApplicationDbContext dbContext) : IReminderService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<ReminderResponse>> CreateReminderAsync(
            CreateMyReminderRequest request,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var reminder = new Reminder
            {
                OrganizationId = organizationId,
                Text = request.Text,
                Scope = ReminderScope.Personal,
                Channel = ReminderChannel.User,
                CreatedByUserId = userId,
                TargetUserId = userId,
                ReminderAtUtc = request.ReminderAtUtc,
                Status = ReminderStatus.Active
            };

            reminder.RaiseDomainEvent(new ReminderCreatedEvent(organizationId, reminder.Id));

            _dbContext.Reminders.Add(reminder);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(reminder.Adapt<ReminderResponse>());
        }

        public async Task<Result<ReminderListResponse>> GetMyRemindersAsync(
            Guid userId,
            Guid organizationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var normalizedPage = Math.Max(page, 1);
            var clampedPageSize = Math.Clamp(pageSize, 1, 50);

            var personalQuery = _dbContext.Reminders
                .Where(r => r.OrganizationId == organizationId)
                .Where(r => r.Scope == ReminderScope.Personal
                         && r.TargetUserId == userId
                         && r.Status == ReminderStatus.Active
                         && r.ReminderAtUtc <= now);

            var publicQuery = _dbContext.Reminders
                .Where(r => r.OrganizationId == organizationId)
                .Where(r => r.Scope == ReminderScope.Public
                         && r.Status == ReminderStatus.Active
                         && r.ReminderAtUtc <= now
                         && r.MeetingId != null)
                .Join(
                    _dbContext.MeetingParticipants.Where(mp => mp.UserId == userId),
                    r => r.MeetingId,
                    mp => mp.MeetingId,
                    (r, mp) => r)
                .Join(
                    _dbContext.Meetings.Where(m => m.Status != MeetingStatus.Cancelled),
                    r => r.MeetingId,
                    m => m.Id,
                    (r, m) => r);

            var combinedQuery = personalQuery
                .Union(publicQuery)
                .OrderBy(r => r.ReminderAtUtc);

            var totalCount = await combinedQuery.CountAsync(cancellationToken);

            var items = await combinedQuery
                .Skip((normalizedPage - 1) * clampedPageSize)
                .Take(clampedPageSize)
                .ToListAsync(cancellationToken);

            var responses = items.Adapt<List<ReminderResponse>>();

            return Result.Success(new ReminderListResponse(responses, totalCount, normalizedPage, clampedPageSize));
        }

        public async Task<Result<ReminderResponse>> MarkDeliveredAsync(
            Guid reminderId,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var reminder = await _dbContext.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.OrganizationId == organizationId, cancellationToken);

            if (reminder == null)
                return Result.Failure<ReminderResponse>(ReminderErrors.NotFound);

            if (reminder.Scope != ReminderScope.Personal)
                return Result.Failure<ReminderResponse>(ReminderErrors.NotPersonal);

            if (reminder.TargetUserId != userId)
                return Result.Failure<ReminderResponse>(ReminderErrors.NotOwner);

            if (reminder.Status == ReminderStatus.Delivered)
                return Result.Success(reminder.Adapt<ReminderResponse>());

            if (reminder.Status == ReminderStatus.Cancelled)
                return Result.Failure<ReminderResponse>(ReminderErrors.InvalidStatus);

            reminder.Status = ReminderStatus.Delivered;
            reminder.DeliveredAtUtc = DateTime.UtcNow;

            reminder.RaiseDomainEvent(new ReminderDeliveredEvent(organizationId, reminder.Id));
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(reminder.Adapt<ReminderResponse>());
        }

        public async Task<Result> CancelReminderAsync(
            Guid reminderId,
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var reminder = await _dbContext.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.OrganizationId == organizationId, cancellationToken);

            if (reminder == null)
                return Result.Failure(ReminderErrors.NotFound);

            if (reminder.Scope != ReminderScope.Personal)
                return Result.Failure(ReminderErrors.NotPersonal);

            if (reminder.TargetUserId != userId)
                return Result.Failure(ReminderErrors.NotOwner);

            if (reminder.Status == ReminderStatus.Delivered)
                return Result.Failure(ReminderErrors.AlreadyDelivered);

            if (reminder.Status == ReminderStatus.Cancelled)
                return Result.Failure(ReminderErrors.AlreadyCancelled);

            reminder.Status = ReminderStatus.Cancelled;

            reminder.RaiseDomainEvent(new ReminderCancelledEvent(organizationId, reminder.Id));
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }
}
