using FluentValidation;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class RefreshTokenRequestValidator: AbstractValidator<RefreshTokenRequest>
    {
        public RefreshTokenRequestValidator()
        {
           RuleFor(x => x.Token).NotEmpty();
           RuleFor(x => x.RefreshToken).NotEmpty();
        }
    
    }
}
