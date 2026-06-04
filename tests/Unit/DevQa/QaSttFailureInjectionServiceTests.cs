using FluentAssertions;
using MeetingAssistant.Features.DevQa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace tests.Unit.DevQa;

public sealed class QaSttFailureInjectionServiceTests
{
    [Fact]
    public void TryInjectFailure_ShouldThrowForConfiguredFailCountThenDelegate()
    {
        var service = CreateService();
        var meetingId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();
        const string objectKey = "qa/mtg:test/user:bob/track-bob.wav";

        service.SetRules(
            meetingId,
            organizationId,
            [new QaSttFailureRuleRequest(objectKey, FailCount: 1, Mode: "throw", Message: "QA injected STT failure")]);

        var act = () => service.TryInjectFailure(meetingId, organizationId, participantUserId, objectKey);
        act.Should().Throw<InvalidOperationException>().WithMessage("QA injected STT failure");

        var afterFirst = service.GetState(meetingId, organizationId);
        afterFirst.Attempts.Should().ContainSingle();
        afterFirst.Attempts[0].Outcome.Should().Be("injected_failure");
        afterFirst.Attempts[0].AttemptNumber.Should().Be(1);

        service.TryInjectFailure(meetingId, organizationId, participantUserId, objectKey);

        var afterSecond = service.GetState(meetingId, organizationId);
        afterSecond.Attempts.Should().HaveCount(2);
        afterSecond.Attempts[1].Outcome.Should().Be("delegated");
        afterSecond.Attempts[1].AttemptNumber.Should().Be(2);
    }

    [Fact]
    public void TryInjectFailure_ShouldNoOpForUnconfiguredObjectKey()
    {
        var service = CreateService();
        var meetingId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();

        service.SetRules(
            meetingId,
            organizationId,
            [new QaSttFailureRuleRequest("qa/other.wav", FailCount: 1)]);

        var act = () => service.TryInjectFailure(meetingId, organizationId, Guid.NewGuid(), "qa/unmatched.wav");
        act.Should().NotThrow();

        service.GetState(meetingId, organizationId).Attempts.Should().BeEmpty();
    }

    private static QaSttFailureInjectionService CreateService()
    {
        var environment = new TestHostEnvironment();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new QaSttFailureInjectionService(environment, configuration);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
