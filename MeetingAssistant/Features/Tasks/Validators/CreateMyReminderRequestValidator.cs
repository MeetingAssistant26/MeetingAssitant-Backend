using FluentValidation;

namespace MeetingAssistant.Features.Tasks.Validators
{
    public class CreateMyReminderRequestValidator : AbstractValidator<Contracts.Requests.CreateMyReminderRequest>
    {
        public CreateMyReminderRequestValidator()
        {
            RuleFor(x => x.Text)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Text is required.")
                .MaximumLength(500).WithMessage("Text cannot exceed 500 characters.");

            RuleFor(x => x.ReminderAtUtc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("ReminderAtUtc is required.")
                .Must(reminderAtUtc => reminderAtUtc.Kind == DateTimeKind.Utc)
                .WithMessage("ReminderAtUtc must be specified as a UTC datetime.")
                .GreaterThan(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .WithMessage("ReminderAtUtc must be after 2000-01-01T00:00:00Z.");
        }
    }
}
