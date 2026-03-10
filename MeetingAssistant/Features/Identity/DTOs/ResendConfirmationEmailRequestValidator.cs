using FluentValidation;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class ResendConfirmationEmailRequestValidator: AbstractValidator<ResendConfirmationEmailRequest>
    {
        public ResendConfirmationEmailRequestValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty()
                .EmailAddress();


        }
    }
   
}
