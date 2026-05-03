using FluentValidation;
using MeetingAssistant.Features.AgentApi.Models.Requests;

namespace MeetingAssistant.Features.AgentApi.Validators
{
    public class CreateAgentReminderRequestValidator : AbstractValidator<CreateAgentReminderRequest>
    {
        public CreateAgentReminderRequestValidator()
        {
            RuleFor(x => x.Text)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Text is required.")
                .MaximumLength(500).WithMessage("Text cannot exceed 500 characters.");

            RuleFor(x => x.Scope)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Scope is required.")
                .Must(scope => string.Equals(scope, "Personal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(scope, "Public", StringComparison.OrdinalIgnoreCase))
                .WithMessage("Scope must be either Personal or Public.");

            RuleFor(x => x.TargetUserId)
                .NotNull()
                .When(x => string.Equals(x.Scope, "Personal", StringComparison.OrdinalIgnoreCase))
                .WithMessage("TargetUserId is required when Scope is Personal.");

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
