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
                .ForEach(email => email.NotEmpty().EmailAddress());
        }
    }
}
