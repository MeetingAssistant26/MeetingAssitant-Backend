using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;

namespace MeetingAssistant.Features.Meetings.Validators
{
    public class AddParticipantRequestValidator : AbstractValidator<AddParticipantRequest>
    {
        public AddParticipantRequestValidator()
        {
            RuleFor(x => x.UserId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("UserId is required.");

            RuleFor(x => x.MeetingRole)
                .Cascade(CascadeMode.Stop)
                .IsInEnum().WithMessage("Role must be a valid MeetingRole.");
        }
    }
}