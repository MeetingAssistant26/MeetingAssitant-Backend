using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;

namespace MeetingAssistant.Features.Meetings.Validators
{
    public class UpdateMeetingRequestValidator : AbstractValidator<UpdateMeetingRequest>
    {
        public UpdateMeetingRequestValidator()
        {
            RuleFor(x => x.Title)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Title cannot be empty.")
                .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.")
                .When(x => x.Title != null);

            RuleFor(x => x.Description)
                .Cascade(CascadeMode.Stop)
                .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.")
                .When(x => x.Description != null);

            RuleFor(x => x.ScheduledStartUtc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Start time cannot be empty.")
                .GreaterThan(DateTime.UtcNow).WithMessage("Start time must be in the future.")
                .When(x => x.ScheduledStartUtc.HasValue);

            RuleFor(x => x.ScheduledEndUtc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("End time cannot be empty.")
                .GreaterThan(x => x.ScheduledStartUtc).WithMessage("End time must be after the start time.")
                .When(x => x.ScheduledEndUtc.HasValue && x.ScheduledStartUtc.HasValue);
        }
    }
}