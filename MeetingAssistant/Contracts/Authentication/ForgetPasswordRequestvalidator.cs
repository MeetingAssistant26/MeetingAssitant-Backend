using FluentValidation;

namespace MeetingAssistant.Contracts.Authentication
{
    public class ForgetPasswordRequestvalidator:AbstractValidator<ForgetPasswordRequest>
    {
        public ForgetPasswordRequestvalidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty()
                .EmailAddress();


        }
    
    }
}
