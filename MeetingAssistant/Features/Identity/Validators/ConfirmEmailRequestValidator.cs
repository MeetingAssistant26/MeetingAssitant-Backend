using FluentValidation;

namespace MeetingAssistant.Features.Identity.DTOs
{
    public class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
    {
        public ConfirmEmailRequestValidator()
        {
            RuleFor(x => x.UserId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Must(id => Guid.TryParse(id, out _))
                .WithMessage("Invalid ID format");
           

            RuleFor(x => x.Code)
                .NotEmpty();
        }
    }
}
