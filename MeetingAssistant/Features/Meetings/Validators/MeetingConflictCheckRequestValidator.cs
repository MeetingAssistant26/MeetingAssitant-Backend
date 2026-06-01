using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;

namespace MeetingAssistant.Features.Meetings.Validators;

public sealed class MeetingConflictCheckRequestValidator : AbstractValidator<MeetingConflictCheckRequest>
{
    public MeetingConflictCheckRequestValidator()
    {
        RuleFor(x => x.ScheduledStartUtc)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Start time is required.");

        RuleFor(x => x.ScheduledEndUtc)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("End time is required.")
            .GreaterThan(x => x.ScheduledStartUtc).WithMessage("End time must be after the start time.");

        RuleForEach(x => x.ParticipantUserIds)
            .NotEmpty().WithMessage("Participant user IDs must be valid.");
    }
}
