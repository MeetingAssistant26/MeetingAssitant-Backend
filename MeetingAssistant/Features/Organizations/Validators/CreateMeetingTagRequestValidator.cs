using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class CreateMeetingTagRequestValidator : AbstractValidator<CreateMeetingTagRequest>
    {
        public CreateMeetingTagRequestValidator()
        {
            RuleFor(x => x.Name)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Name is required.")
                .MaximumLength(50).WithMessage("Name cannot exceed 50 characters.");

            RuleFor(x => x.Color)
                .Matches(MeetingTagColor.ValidationPattern).WithMessage("Color must be a valid hex code (e.g., #CCC or #FF0000).")
                .When(x => !string.IsNullOrEmpty(x.Color));
        }
    }
}
