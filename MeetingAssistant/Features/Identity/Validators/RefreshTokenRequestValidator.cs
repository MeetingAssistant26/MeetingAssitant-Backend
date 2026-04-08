using FluentValidation;
using MeetingAssistant.Features.Identity.Models.Requests;

namespace MeetingAssistant.Features.Identity.Validators
{
    public class RefreshTokenRequestValidator: AbstractValidator<RefreshTokenRequest>
    {
        public RefreshTokenRequestValidator()
        {
           RuleFor(x => x.RefreshToken)
               .Cascade(CascadeMode.Stop)
               .NotEmpty();
        }
    }
}
