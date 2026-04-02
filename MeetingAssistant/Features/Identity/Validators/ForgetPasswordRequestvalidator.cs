using FluentValidation;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class ForgetPasswordRequestvalidator:AbstractValidator<ForgetPasswordRequest>
    {
        public ForgetPasswordRequestvalidator()
        {
            RuleFor(x => x.Email)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .EmailAddress();


        }
    
    }
}
