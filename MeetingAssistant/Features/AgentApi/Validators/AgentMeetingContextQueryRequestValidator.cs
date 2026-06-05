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
        public const int MaxConversationTurns = 8;
        public const int MaxConversationTurnTextLength = 2_000;
        public const int MaxConversationTotalTextLength = 8_000;

        private static readonly HashSet<string> AllowedConversationRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "user",
            "assistant"
        };

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

            RuleFor(x => x.ConversationTurns)
                .Must(turns => turns is null || turns.Count <= MaxConversationTurns)
                .WithMessage($"ConversationTurns cannot contain more than {MaxConversationTurns} turns.");

            RuleFor(x => x.ConversationTurns)
                .Must(turns => turns is null || turns.Sum(turn => turn?.Text?.Length ?? 0) <= MaxConversationTotalTextLength)
                .WithMessage($"Conversation turns cannot exceed {MaxConversationTotalTextLength} total characters.");

            RuleForEach(x => x.ConversationTurns)
                .NotNull()
                .WithMessage("ConversationTurns cannot contain null values.");

            RuleForEach(x => x.ConversationTurns).ChildRules(turn =>
            {
                turn.RuleFor(x => x.Role)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty().WithMessage("Conversation turn role is required.")
                    .Must(role => !string.IsNullOrWhiteSpace(role) && AllowedConversationRoles.Contains(role))
                    .WithMessage("Conversation turn role must be 'user' or 'assistant'.");

                turn.RuleFor(x => x.Text)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty().WithMessage("Conversation turn text is required.")
                    .MaximumLength(MaxConversationTurnTextLength)
                    .WithMessage($"Conversation turn text cannot exceed {MaxConversationTurnTextLength} characters.");
            });
        }
    }
}
