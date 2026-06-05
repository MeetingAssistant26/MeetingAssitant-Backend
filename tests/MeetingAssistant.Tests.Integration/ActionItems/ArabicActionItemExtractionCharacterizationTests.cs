using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using MeetingAssistant.Tests.Integration.ActionItems.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tests.Integration.LiveSession;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

/// <summary>
/// Integration tests for improved Arabic action-item extraction using production-inspired
/// fixtures from meetings e7f2429c-4d6e-497e-a205-58078b853c90 and
/// 62d5ef74-4093-47be-814e-1f779901ed95.
/// </summary>
public sealed class ArabicActionItemExtractionCharacterizationTests
{
    [Fact]
    public async Task RunAsync_E7f2429cProductionFixture_AssignsOrgMembersAndNormalizesArabicDeadlines()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var ibrahimId = db.SeedUser("Ibrahim");
        var assignees = SeedArabicOrgMembers(db, organizationId);
        var meetingId = SeedMeetingWithReferenceDate(db, organizationId);
        var ibrahimParticipantId = db.AddParticipant(meetingId, organizationId, ibrahimId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = ArabicActionItemProductionFixtures.E7f2429c.TranscriptFullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var llm = new CapturingLlmService(ArabicActionItemProductionFixtures.E7f2429c.BuildImprovedLlmJson(assignees));
        var job = CreateJob(db, llm);
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        var prompt = llm.LastRequest!.Messages.Should().ContainSingle().Subject.Content;
        prompt.Should().Contain("Meeting reference date (UTC): 2026-06-01");
        prompt.Should().Contain("Timezone for deadline normalization: UTC");
        prompt.Should().Contain($"user_id={assignees.Ahmed.UserId}");
        prompt.Should().Contain($"job_role={assignees.Ahmed.JobRole}");
        prompt.Should().Contain($"user_id={assignees.Salma.UserId}; participant_id=null");
        prompt.Should().Contain($"participant_id={ibrahimParticipantId}");
        prompt.Should().Contain($"display_name={assignees.Youssef.DisplayName}");

        var items = await db.DbContext.ActionItems
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId && x.SupersededAtUtc == null)
            .OrderBy(x => x.Title)
            .ToListAsync();

        items.Should().HaveCount(5);
        items.Should().OnlyContain(x => x.Status == ActionItemStatus.PendingReview);
        items.Should().OnlyContain(x => x.SyncMissingAssigneeReason == null);
        items.Should().OnlyContain(x => x.AiRawDeadlineText != null);
        items.Should().OnlyContain(x => x.AiDeadlineConfidence == 0.91m);
        items.Should().OnlyContain(x => x.AiAssigneeConfidence == 0.92m);

        AssertAssigned(items, "تجهيز تقرير الأداء", assignees.Ahmed.UserId, new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc), "18 يونيو");
        AssertAssigned(items, "متابعة العملاء", assignees.Salma.UserId, new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc), "20 يونيو");
        AssertAssigned(items, "مراجعة الاختبارات", assignees.Youssef.UserId, new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc), "22 يونيو");
        AssertAssigned(items, "تجهيز العرض", assignees.Nour.UserId, new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc), "25 يونيو");
        AssertAssigned(items, "إرسال التحديثات", assignees.Mahmoud.UserId, new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc), "21 يونيو");
    }

    [Fact]
    public async Task RunAsync_62d5ef74ProductionFixture_AssignsNamedTasksAndLeavesFeedbackItemUnassigned()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var ibrahimId = db.SeedUser("Ibrahim");
        var assignees = SeedArabicOrgMembers(db, organizationId);
        var meetingId = SeedMeetingWithReferenceDate(db, organizationId);
        var ibrahimParticipantId = db.AddParticipant(meetingId, organizationId, ibrahimId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = ArabicActionItemProductionFixtures.Meeting62d5ef74.TranscriptFullText,
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(
            db,
            new CapturingLlmService(ArabicActionItemProductionFixtures.Meeting62d5ef74.BuildImprovedLlmJson(assignees)));
        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var items = await db.DbContext.ActionItems
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId && x.SupersededAtUtc == null)
            .OrderBy(x => x.Title)
            .ToListAsync();

        items.Should().HaveCount(6);

        AssertAssigned(items, "إكمال التكامل", assignees.Mohamed.UserId, new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc), "28 يونيو");
        AssertAssigned(items, "مراجعة الواجهة", assignees.Karim.UserId, new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), "30 يونيو");
        AssertAssigned(items, "تعديل التوثيق", assignees.Fatima.UserId, new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc), "25 يونيو");
        AssertAssigned(items, "اختبار الإصدار", assignees.Omar.UserId, new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc), "2 يوليو");
        AssertAssigned(items, "تنسيق مع الفريق", assignees.Mohamed.UserId, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc), "5 يوليو");

        var feedbackItem = items.Single(x => x.Title == "تجميع ملاحظات المستخدمين");
        feedbackItem.AssignedToUserId.Should().BeNull();
        feedbackItem.AssignedToParticipantId.Should().BeNull();
        feedbackItem.DueDateUtc.Should().BeNull();
        feedbackItem.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.NeedsAssignee);
        feedbackItem.AiRawAssigneeText.Should().BeNull();
        feedbackItem.AiRawDeadlineText.Should().BeNull();
    }

    private static void AssertAssigned(
        IReadOnlyList<MeetingAssistant.Features.ActionItems.Models.Entities.ActionItem> items,
        string title,
        Guid expectedUserId,
        DateTime expectedDueDateUtc,
        string expectedRawDeadline)
    {
        var item = items.Single(x => x.Title == title);
        item.AssignedToUserId.Should().Be(expectedUserId);
        item.DueDateUtc.Should().Be(expectedDueDateUtc);
        item.AiRawAssigneeText.Should().NotBeNullOrWhiteSpace();
        item.AiRawDeadlineText.Should().Be(expectedRawDeadline);
        item.AiSuggestedAssignedToUserId.Should().Be(expectedUserId);
        item.AiSuggestedDueDateUtc.Should().Be(expectedDueDateUtc);
        item.Description.Should().Contain("AI assignee:");
        item.Description.Should().Contain("AI due date:");
        item.Description.Should().NotContain("confidence");
    }

    private static Guid SeedMeetingWithReferenceDate(LiveSessionTestDb db, Guid organizationId)
    {
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
        meeting.RoomActivatedAtUtc = ArabicActionItemProductionFixtures.MeetingReferenceDateUtc;
        meeting.ScheduledStartUtc = ArabicActionItemProductionFixtures.MeetingReferenceDateUtc;
        db.DbContext.SaveChanges();
        return meetingId;
    }

    private static ArabicAssigneeFixtureSet SeedArabicOrgMembers(LiveSessionTestDb db, Guid organizationId)
    {
        ArabicAssigneeFixture Create(string rawName, string displayName, string jobRole, string context)
        {
            var userId = SeedUserWithDisplayName(db, displayName);
            db.DbContext.UserOrgMemberships.Add(new UserOrgMembership
            {
                OrganizationId = organizationId,
                UserId = userId,
                OrgRole = OrganizationRole.Member,
                JobRole = jobRole,
                Context = context,
                ContextStatus = ContextStatus.Processed,
                IsEnabled = true
            });
            db.DbContext.SaveChanges();
            return new ArabicAssigneeFixture(userId, null, rawName, displayName, jobRole, context);
        }

        return new ArabicAssigneeFixtureSet(
            Ahmed: Create("أحمد", "Ahmed Hassan", "Performance Analyst", "Owns monthly KPI reporting."),
            Salma: Create("سلمى", "Salma Nabil", "Customer Success Lead", "Handles enterprise client follow-ups."),
            Youssef: Create("يوسف", "Youssef Ali", "QA Engineer", "Owns regression testing before release."),
            Nour: Create("نور", "Nour Hatem", "Product Designer", "Prepares customer-facing decks."),
            Mahmoud: Create("محمود", "Mahmoud Saad", "Operations Coordinator", "Sends weekly operational updates."),
            Mohamed: Create("محمد", "Mohamed Farid", "Integration Engineer", "Owns backend integration work."),
            Karim: Create("كريم", "Karim Adel", "Frontend Engineer", "Owns UI review and polish."),
            Fatima: Create("فاطمة", "Fatima Omar", "Technical Writer", "Maintains product documentation."),
            Omar: Create("عمر", "Omar Rashid", "Release Engineer", "Runs release validation."));
    }

    private static Guid SeedUserWithDisplayName(LiveSessionTestDb db, string displayName)
    {
        var user = new MeetingAssistant.Features.Identity.Entites.ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{displayName.Replace(' ', '-')}-{Guid.NewGuid():N}@example.com",
            Email = $"{displayName.Replace(' ', '-')}-{Guid.NewGuid():N}@example.com",
            DisplayName = displayName
        };
        db.DbContext.Users.Add(user);
        db.DbContext.SaveChanges();
        return user.Id;
    }

    private static ExtractActionItemsJob CreateJob(LiveSessionTestDb db, ILLMService llmService)
    {
        return new ExtractActionItemsJob(
            db.DbContext,
            llmService,
            new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory)),
            Options.Create(new OpenAiCompatibleOptions
            {
                Llm = new OpenAiCompatibleOptions.ProviderConfig
                {
                    BaseUrl = "http://llm.test/v1",
                    ApiKey = "test-key",
                    Model = "openai-compatible-local"
                }
            }),
            NullLogger<ExtractActionItemsJob>.Instance,
            postMeetingProcessingTracker: null,
            hangfireJobContextAccessor: null,
            backgroundJobClient: null);
    }

    private sealed class CapturingLlmService(string content) : ILLMService
    {
        public LLMRequest? LastRequest { get; private set; }

        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new LLMResponse
            {
                Choices =
                [
                    new Choice
                    {
                        Message = new ChoiceMessage
                        {
                            Role = "assistant",
                            Content = content
                        }
                    }
                ]
            });
        }

        public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ExtractActionItemsJob should parse the ai_work tasks schema itself.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
