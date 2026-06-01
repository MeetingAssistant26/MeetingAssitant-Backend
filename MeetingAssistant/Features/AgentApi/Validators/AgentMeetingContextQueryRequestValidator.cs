using FluentValidation;
using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Features.Rag.Models;

namespace MeetingAssistant.Features.AgentApi.Validators
{
    public sealed class AgentMeetingContextQueryRequestValidator : AbstractValidator<AgentMeetingContextQueryRequest>
    {
        public const int DefaultTopK = 5;
        public const int MaxTopK = 10;
        public const int MaxPreferredTagIds = 20;
        public const int MaxSourceTypes = 8;

        public AgentMeetingContextQueryRequestValidator()
        {
            RuleFor(x => x.Question)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Question is required.")
                .MaximumLength(2_000).WithMessage("Question cannot exceed 2000 characters.");

            RuleFor(x => x.Transcript)
                .MaximumLength(10_000).WithMessage("Transcript cannot exceed 10000 characters.")
                .When(x => x.Transcript is not null);

            RuleFor(x => x.TopK)
                .InclusiveBetween(1, MaxTopK)
                .When(x => x.TopK.HasValue)
                .WithMessage($"TopK must be between 1 and {MaxTopK}.");

            RuleFor(x => x.PreferredTagIds)
                .Must(ids => ids is null || ids.Count <= MaxPreferredTagIds)
                .WithMessage($"PreferredTagIds cannot contain more than {MaxPreferredTagIds} values.");

            RuleForEach(x => x.PreferredTagIds)
                .NotEmpty().WithMessage("PreferredTagIds cannot contain an empty value.");

            RuleFor(x => x.SourceTypes)
                .Must(types => types is null || types.Count <= MaxSourceTypes)
                .WithMessage($"SourceTypes cannot contain more than {MaxSourceTypes} values.");

            RuleForEach(x => x.SourceTypes)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("SourceTypes cannot contain an empty value.")
                .Must(type => Enum.TryParse<KnowledgeArtifactType>(type, ignoreCase: true, out _))
                .WithMessage("SourceTypes must contain valid knowledge source types.");
        }
    }
}
