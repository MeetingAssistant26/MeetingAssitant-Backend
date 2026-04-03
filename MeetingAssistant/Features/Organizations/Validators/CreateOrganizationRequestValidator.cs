using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
    {
        public CreateOrganizationRequestValidator()
        {
            RuleFor(x => x.Name)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Length(1, 200);
        }
    }
}
