using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
    {
        public CreateInvitationRequestValidator()
        {
            RuleFor(x => x.EmailWhitelist)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Must(emails => emails.Distinct(StringComparer.OrdinalIgnoreCase).Count() == emails.Count)
                    .WithMessage("Duplicate emails are not allowed.")
                .ForEach(email => email.NotEmpty().EmailAddress());
        }
    }
}
