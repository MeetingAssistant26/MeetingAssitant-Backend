using FluentValidation;
using MeetingAssistant.Features.Identity.Models.Requests;

namespace MeetingAssistant.Features.Identity.Validators
{
    public class LogoutRequestValidator : AbstractValidator<LogoutRequest>
    {
        public LogoutRequestValidator()
        {
            RuleFor(x => x.RefreshToken)
                .Cascade(CascadeMode.Stop)
                .NotEmpty();
        }
    }
}