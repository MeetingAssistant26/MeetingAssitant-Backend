using FluentValidation;

namespace MeetingAssistant.Features.ActionItems.Validators
{
    public class SaveIntegrationDestinationRequestValidator
        : AbstractValidator<Models.Requests.SaveIntegrationDestinationRequest>
    {
        public SaveIntegrationDestinationRequestValidator()
        {
            RuleFor(x => x.WorkspaceId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("WorkspaceId is required.");

            RuleFor(x => x.BoardId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("BoardId is required.");

            RuleFor(x => x.ListId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("ListId is required.");
        }
    }
}
