using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;

namespace MeetingAssistant.Features.Meetings.Validators
{
    public class CreateMeetingRequestValidator : AbstractValidator<CreateMeetingRequest>
    {
        public CreateMeetingRequestValidator()
        {
            RuleFor(x => x.Title)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Title is required.")
                .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.");

            RuleFor(x => x.Description)
                .Cascade(CascadeMode.Stop)
                .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

            RuleFor(x => x.ScheduledStartUtc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Start time is required.")
                .GreaterThan(DateTime.UtcNow).WithMessage("Start time must be in the future.");

            RuleFor(x => x.ScheduledEndUtc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("End time is required.")
                .GreaterThan(x => x.ScheduledStartUtc).WithMessage("End time must be after the start time.");
        }
    }
}