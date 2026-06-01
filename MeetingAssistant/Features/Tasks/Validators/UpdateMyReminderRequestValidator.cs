using FluentValidation;
using MeetingAssistant.Features.Tasks.Contracts.Requests;

namespace MeetingAssistant.Features.Tasks.Validators
{
    public class UpdateMyReminderRequestValidator : AbstractValidator<UpdateMyReminderRequest>
    {
        public UpdateMyReminderRequestValidator()
        {
            RuleFor(x => x)
                .Must(x => x.Text is not null || x.ReminderAtUtc.HasValue)
                .WithMessage("At least one field must be provided.");

            RuleFor(x => x.Text)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Text cannot be empty.")
                .MaximumLength(500).WithMessage("Text cannot exceed 500 characters.")
                .When(x => x.Text is not null);

            RuleFor(x => x.ReminderAtUtc!.Value)
                .Cascade(CascadeMode.Stop)
                .Must(reminderAtUtc => reminderAtUtc.Kind == DateTimeKind.Utc)
                .WithMessage("ReminderAtUtc must be specified as a UTC datetime.")
                .GreaterThan(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .WithMessage("ReminderAtUtc must be after 2000-01-01T00:00:00Z.")
                .When(x => x.ReminderAtUtc.HasValue);
        }
    }
}
