using FluentValidation;

namespace MeetingAssistant.Features.ActionItems.Validators
{
    public class ConnectProviderRequestValidator : AbstractValidator<Models.Requests.ConnectProviderRequest>
    {
        public ConnectProviderRequestValidator()
        {
            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Token is required.");
        }
    }
}
