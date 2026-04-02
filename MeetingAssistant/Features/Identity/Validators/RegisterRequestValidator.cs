using FluentValidation;
using MeetingAssistant.Shared.Abstractions.Consts;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class RegisterRequestValidator:AbstractValidator<RegisterRequest>
    {
        public RegisterRequestValidator()
        {
            RuleFor(x => x.Email)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Must(BeValidEmail)
                .WithMessage("Invalid Email Format");

            RuleFor(x => x.Password)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Matches(RegexPatterns.Password)
                .WithMessage("password should be at least 8 digits and contains LowerCase,NonAlpanumeric and UpperCase");

            RuleFor(x => x.DisplayName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Matches(@"^[a-zA-Z0-9\s\.\-_]+$")
                .Length(3, 100);
        }

        private bool BeValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

    }
}
