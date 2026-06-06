using FluentValidation;

namespace MeetingAssistant.Features.ActionItems.Validators
{
    public class SaveProviderCredentialsRequestValidator
        : AbstractValidator<Models.Requests.SaveProviderCredentialsRequest>
    {
        public SaveProviderCredentialsRequestValidator()
        {
            RuleFor(x => x.ApiKey)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("ApiKey is required.");

            RuleFor(x => x.ApiToken)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("ApiToken is required.");
        }
    }
}
