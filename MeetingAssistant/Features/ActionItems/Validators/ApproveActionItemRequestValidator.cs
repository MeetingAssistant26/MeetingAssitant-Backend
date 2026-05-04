using FluentValidation;

namespace MeetingAssistant.Features.ActionItems.Validators
{
    public class ApproveActionItemRequestValidator : AbstractValidator<Models.Requests.ApproveActionItemRequest>
    {
        public ApproveActionItemRequestValidator()
        {
        }
    }
}
