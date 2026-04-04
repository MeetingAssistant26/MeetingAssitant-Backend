using FluentValidation;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Models;

namespace MeetingAssistant.Features.Organizations.Validators
{
    public class UpdateMemberRoleRequestValidator : AbstractValidator<UpdateMemberRoleRequest>
    {
        public UpdateMemberRoleRequestValidator()
        {
            RuleFor(x => x.OrgRole)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .IsEnumName(typeof(OrganizationRole), caseSensitive: false);
        }
    }
}
