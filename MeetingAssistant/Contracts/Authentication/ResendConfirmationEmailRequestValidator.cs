using FluentValidation;

namespace MeetingAssistant.Contracts.Authentication
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
