using FluentValidation;
using MeetingAssistant.Features.LiveSession.Contracts.Requests;

namespace MeetingAssistant.Features.LiveSession.Validators
{
    public class JoinTokenRequestValidator : AbstractValidator<JoinTokenRequest>
    {
        public JoinTokenRequestValidator()
        {
            When(x => x.DisplayName is not null, () =>
            {
                RuleFor(x => x.DisplayName!)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty().WithMessage("Display name cannot be empty.")
                    .MaximumLength(120).WithMessage("Display name cannot exceed 120 characters.");
            });
        }
    }
}
