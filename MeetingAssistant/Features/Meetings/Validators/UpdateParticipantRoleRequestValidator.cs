using FluentValidation;
using MeetingAssistant.Features.Meetings.Contracts.Requests;

namespace MeetingAssistant.Features.Meetings.Validators
{
    public sealed class UpdateParticipantRoleRequestValidator : AbstractValidator<UpdateParticipantRoleRequest>
    {
        public UpdateParticipantRoleRequestValidator()
        {
            RuleFor(x => x.MeetingRole)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("MeetingRole is required.")
                .IsInEnum().WithMessage("Role must be a valid MeetingRole.");
        }
    }
}
