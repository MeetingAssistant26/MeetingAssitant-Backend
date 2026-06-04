using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MeetingAssistant.Features.DevQa;

public interface IQaSttFailureInjectionService
{
    bool IsActive { get; }

    void SetRules(Guid meetingId, Guid organizationId, IReadOnlyList<QaSttFailureRuleRequest> rules);

    QaSttFailureStateResponse GetState(Guid meetingId, Guid organizationId);

    /// <summary>
    /// Records an STT attempt and throws when a matching DevQa rule still has remaining failures.
    /// No-op when QA harness injection is disabled.
    /// </summary>
    void TryInjectFailure(
        Guid meetingId,
        Guid organizationId,
        Guid participantUserId,
        string storageObjectKey);
}

public sealed class QaSttFailureInjectionService(
    IHostEnvironment environment,
    IConfiguration configuration) : IQaSttFailureInjectionService
{
    private readonly object _sync = new();
    private readonly Dictionary<QaSttFailureMeetingKey, QaSttFailureMeetingState> _meetings = new();

    public bool IsActive => IsQaHarnessEnabled();

    public void SetRules(Guid meetingId, Guid organizationId, IReadOnlyList<QaSttFailureRuleRequest> rules)
    {
        EnsureHarnessEnabled();

        var key = new QaSttFailureMeetingKey(meetingId, organizationId);
        var normalizedRules = (rules ?? [])
            .Where(rule => !string.IsNullOrWhiteSpace(rule.StorageObjectKey))
            .Select(rule => new QaSttFailureRuleState(
                rule.StorageObjectKey!.Trim(),
                Math.Max(1, rule.FailCount ?? 1),
                NormalizeMode(rule.Mode),
                string.IsNullOrWhiteSpace(rule.Message)
                    ? "QA injected STT failure for retry capture"
                    : rule.Message!.Trim()))
            .ToList();

        lock (_sync)
        {
            _meetings[key] = new QaSttFailureMeetingState(
                meetingId,
                organizationId,
                normalizedRules,
                []);
        }
    }

    public QaSttFailureStateResponse GetState(Guid meetingId, Guid organizationId)
    {
        EnsureHarnessEnabled();

        var key = new QaSttFailureMeetingKey(meetingId, organizationId);
        lock (_sync)
        {
            if (!_meetings.TryGetValue(key, out var state))
            {
                return new QaSttFailureStateResponse(
                    meetingId,
                    organizationId,
                    [],
                    []);
            }

            return new QaSttFailureStateResponse(
                meetingId,
                organizationId,
                state.Rules.Select(rule => new QaSttFailureRuleResponse(
                    rule.StorageObjectKey,
                    rule.FailCount,
                    rule.Mode,
                    rule.Message)).ToList(),
                state.Attempts.Select(attempt => new QaSttFailureAttemptResponse(
                    attempt.StorageObjectKey,
                    attempt.ParticipantUserId,
                    attempt.AttemptNumber,
                    attempt.Outcome,
                    attempt.OccurredAtUtc)).ToList());
        }
    }

    public void TryInjectFailure(
        Guid meetingId,
        Guid organizationId,
        Guid participantUserId,
        string storageObjectKey)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(storageObjectKey))
            return;

        var key = new QaSttFailureMeetingKey(meetingId, organizationId);
        QaSttFailureRuleState? rule;
        int attemptNumber;
        lock (_sync)
        {
            if (!_meetings.TryGetValue(key, out var state))
                return;

            rule = state.Rules.FirstOrDefault(candidate =>
                string.Equals(candidate.StorageObjectKey, storageObjectKey, StringComparison.Ordinal));
            if (rule is null)
                return;

            attemptNumber = state.Attempts.Count(attempt =>
                           string.Equals(attempt.StorageObjectKey, storageObjectKey, StringComparison.Ordinal))
                       + 1;

            if (attemptNumber > rule.FailCount)
            {
                state.Attempts.Add(new QaSttFailureAttemptState(
                    storageObjectKey,
                    participantUserId,
                    attemptNumber,
                    "delegated",
                    DateTime.UtcNow));
                return;
            }

            state.Attempts.Add(new QaSttFailureAttemptState(
                storageObjectKey,
                participantUserId,
                attemptNumber,
                "injected_failure",
                DateTime.UtcNow));
        }

        throw new InvalidOperationException(rule.Message);
    }

    private void EnsureHarnessEnabled()
    {
        if (!IsQaHarnessEnabled())
            throw new InvalidOperationException("QA harness STT failure injection is disabled.");
    }

    private bool IsQaHarnessEnabled()
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return true;

        return !environment.IsProduction()
               && configuration.GetValue<bool>("QaHarness:Enabled");
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "throw", StringComparison.OrdinalIgnoreCase) ? "throw" : "throw";
    }

    private readonly record struct QaSttFailureMeetingKey(Guid MeetingId, Guid OrganizationId);

    private sealed class QaSttFailureMeetingState(
        Guid meetingId,
        Guid organizationId,
        IReadOnlyList<QaSttFailureRuleState> rules,
        List<QaSttFailureAttemptState> attempts)
    {
        public Guid MeetingId { get; } = meetingId;
        public Guid OrganizationId { get; } = organizationId;
        public IReadOnlyList<QaSttFailureRuleState> Rules { get; } = rules;
        public List<QaSttFailureAttemptState> Attempts { get; } = attempts;
    }

    private sealed record QaSttFailureRuleState(
        string StorageObjectKey,
        int FailCount,
        string Mode,
        string Message);

    private sealed record QaSttFailureAttemptState(
        string StorageObjectKey,
        Guid? ParticipantUserId,
        int AttemptNumber,
        string Outcome,
        DateTime OccurredAtUtc);
}

public sealed record QaConfigureSttFailuresRequest(
    Guid OrganizationId,
    IReadOnlyList<QaSttFailureRuleRequest>? Rules = null);

public sealed record QaSttFailureRuleRequest(
    string? StorageObjectKey = null,
    int? FailCount = null,
    string? Mode = null,
    string? Message = null);

public sealed record QaSttFailureStateResponse(
    Guid MeetingId,
    Guid OrganizationId,
    IReadOnlyList<QaSttFailureRuleResponse> Rules,
    IReadOnlyList<QaSttFailureAttemptResponse> Attempts);

public sealed record QaSttFailureRuleResponse(
    string StorageObjectKey,
    int FailCount,
    string Mode,
    string Message);

public sealed record QaSttFailureAttemptResponse(
    string StorageObjectKey,
    Guid? ParticipantUserId,
    int AttemptNumber,
    string Outcome,
    DateTime OccurredAtUtc);
