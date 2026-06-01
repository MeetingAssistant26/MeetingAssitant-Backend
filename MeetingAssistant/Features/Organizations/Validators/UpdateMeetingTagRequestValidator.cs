using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class UpdateMeetingTagRequestValidator : AbstractValidator<UpdateMeetingTagRequest>
    {
        public UpdateMeetingTagRequestValidator()
        {
            RuleFor(x => x.Name!)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Name cannot be empty when provided.")
                .MaximumLength(50).WithMessage("Name cannot exceed 50 characters.")
                .When(x => x.Name != null);

            RuleFor(x => x.Color.Value)
                .Matches(MeetingTagColor.ValidationPattern).WithMessage("Color must be a valid hex code (e.g., #CCC or #FF0000).")
                .When(x => x.Color.HasValue && !string.IsNullOrEmpty(x.Color.Value));
        }
    }
}
