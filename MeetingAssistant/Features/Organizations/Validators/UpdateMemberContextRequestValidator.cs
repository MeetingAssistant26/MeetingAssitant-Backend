using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class UpdateMemberContextRequestValidator : AbstractValidator<UpdateMemberContextRequest>
    {
        public UpdateMemberContextRequestValidator()
        {
            RuleFor(x => x.JobRole)
                .Cascade(CascadeMode.Stop)
                .MaximumLength(100);

            RuleFor(x => x.Context)
                .Cascade(CascadeMode.Stop)
                .MaximumLength(2000);
        }
    }
}