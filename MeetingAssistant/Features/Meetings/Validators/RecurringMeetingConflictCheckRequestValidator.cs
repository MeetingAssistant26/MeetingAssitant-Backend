using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Validators;

public sealed class RecurringMeetingConflictCheckRequestValidator : AbstractValidator<RecurringMeetingConflictCheckRequest>
{
    public RecurringMeetingConflictCheckRequestValidator()
    {
        RuleFor(x => x.ScheduledEndTimeUtc)
            .Cascade(CascadeMode.Stop)
            .GreaterThan(x => x.ScheduledStartTimeUtc).WithMessage("End time must be after the start time.");

        RuleForEach(x => x.ParticipantUserIds)
            .NotEmpty().WithMessage("Participant user IDs must be valid.");

        RuleFor(x => x.Recurrence)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Recurrence configuration is required.");

        When(x => x.Recurrence != null, () =>
        {
            RuleFor(x => x.Recurrence.Frequency)
                .Cascade(CascadeMode.Stop)
                .IsInEnum().WithMessage("Frequency must be a valid RecurrenceFrequency.");

            RuleFor(x => x.Recurrence.Interval)
                .Cascade(CascadeMode.Stop)
                .InclusiveBetween(1, 12).WithMessage("Interval must be between 1 and 12.");

            When(x => x.Recurrence.Frequency == RecurrenceFrequency.Weekly, () =>
            {
                RuleFor(x => x.Recurrence.DaysOfWeek)
                    .Cascade(CascadeMode.Stop)
                    .NotNull().WithMessage("Days of week are required for weekly recurrence.")
                    .NotEmpty().WithMessage("Days of week cannot be empty for weekly recurrence.");
            });

            When(x => x.Recurrence.EndsAtUtc.HasValue, () =>
            {
                RuleFor(x => x.Recurrence.EndsAtUtc!.Value)
                    .Cascade(CascadeMode.Stop)
                    .Must(time => time > DateTime.UtcNow)
                    .WithMessage("Ends at must be in the future.");
            });
        });
    }
}
