using FluentValidation;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class ResendConfirmationEmailRequestValidator: AbstractValidator<ResendConfirmationEmailRequest>
    {
        public ResendConfirmationEmailRequestValidator()
        {
            RuleFor(x => x.Email)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .EmailAddress();


        }
    }
   
}
