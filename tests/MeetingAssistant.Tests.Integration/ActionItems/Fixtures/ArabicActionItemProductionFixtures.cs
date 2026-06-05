using System.Text.Json;

namespace MeetingAssistant.Tests.Integration.ActionItems.Fixtures;

/// <summary>
/// Minimal redacted Arabic transcript snippets and fake LLM JSON derived from production
/// meetings e7f2429c-4d6e-497e-a205-58078b853c90 and 62d5ef74-4093-47be-814e-1f779901ed95.
/// Only Ibrahim was a meeting participant in both production cases; improved extraction seeds
/// organization members for Arabic assignee names and expects ID-based normalization.
/// </summary>
internal static class ArabicActionItemProductionFixtures
{
    public const string E7f2429cMeetingId = "e7f2429c-4d6e-497e-a205-58078b853c90";
    public const string Meeting62d5ef74Id = "62d5ef74-4093-47be-814e-1f779901ed95";
    public static readonly DateTime MeetingReferenceDateUtc = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    public static class E7f2429c
    {
        public const string TranscriptFullText = """
            [00:12:03 Ibrahim] نحتاج أحمد يجهّز تقرير الأداء قبل 18 يونيو.
            [00:14:22 Ibrahim] سلمى تتابع مع العملاء حتى 20 يونيو.
            [00:16:45 Ibrahim] يوسف يراجع الاختبارات قبل 22 يونيو.
            [00:18:10 Ibrahim] نور تجهّز العرض قبل 25 يونيو.
            [00:19:55 Ibrahim] محمود يرسل التحديثات قبل 21 يونيو.
            """;

        public static string BuildImprovedLlmJson(ArabicAssigneeFixtureSet assignees)
            => JsonSerializer.Serialize(new object[]
            {
                Task("تجهيز تقرير الأداء", assignees.Ahmed, "18 يونيو", "2026-06-18"),
                Task("متابعة العملاء", assignees.Salma, "20 يونيو", "2026-06-20"),
                Task("مراجعة الاختبارات", assignees.Youssef, "22 يونيو", "2026-06-22"),
                Task("تجهيز العرض", assignees.Nour, "25 يونيو", "2026-06-25"),
                Task("إرسال التحديثات", assignees.Mahmoud, "21 يونيو", "2026-06-21")
            });
    }

    public static class Meeting62d5ef74
    {
        public const string TranscriptFullText = """
            [00:08:15 Ibrahim] محمد يكمل التكامل قبل 28 يونيو.
            [00:10:02 Ibrahim] كريم يراجع الواجهة حتى 30 يونيو.
            [00:11:40 Ibrahim] فاطمة تعدّل التوثيق قبل 25 يونيو.
            [00:13:22 Ibrahim] عمر يختبر الإصدار قبل 2 يوليو.
            [00:15:05 Ibrahim] محمد ينسّق مع الفريق قبل 5 يوليو.
            [00:16:30 Ibrahim] نحتاج تجميع ملاحظات المستخدمين بدون موعد محدد.
            """;

        public static string BuildImprovedLlmJson(ArabicAssigneeFixtureSet assignees)
            => JsonSerializer.Serialize(new object[]
            {
                Task("إكمال التكامل", assignees.Mohamed, "28 يونيو", "2026-06-28"),
                Task("مراجعة الواجهة", assignees.Karim, "30 يونيو", "2026-06-30"),
                Task("تعديل التوثيق", assignees.Fatima, "25 يونيو", "2026-06-25"),
                Task("اختبار الإصدار", assignees.Omar, "2 يوليو", "2026-07-02"),
                Task("تنسيق مع الفريق", assignees.Mohamed, "5 يوليو", "2026-07-05"),
                new
                {
                    task = "تجميع ملاحظات المستخدمين",
                    responsible_person = (string?)null,
                    assigned_user_id = (Guid?)null,
                    assigned_participant_id = (Guid?)null,
                    assignee_confidence = 0.0,
                    assignee_reason = (string?)null,
                    deadline = (string?)null,
                    deadline_date = (string?)null,
                    deadline_utc = (string?)null,
                    deadline_confidence = 0.0,
                    deadline_reason = (string?)null
                }
            });
    }

    private static object Task(
        string task,
        ArabicAssigneeFixture assignee,
        string rawDeadline,
        string deadlineDate)
        => new
        {
            task,
            responsible_person = assignee.RawName,
            assigned_user_id = assignee.UserId,
            assigned_participant_id = assignee.ParticipantId,
            assignee_confidence = 0.92,
            assignee_reason = $"Matched {assignee.RawName} from organization member context",
            deadline = rawDeadline,
            deadline_date = deadlineDate,
            deadline_utc = $"{deadlineDate}T00:00:00Z",
            deadline_confidence = 0.91,
            deadline_reason = "Normalized Arabic calendar date from meeting reference year"
        };
}

internal sealed record ArabicAssigneeFixture(
    Guid UserId,
    Guid? ParticipantId,
    string RawName,
    string DisplayName,
    string JobRole,
    string Context);

internal sealed record ArabicAssigneeFixtureSet(
    ArabicAssigneeFixture Ahmed,
    ArabicAssigneeFixture Salma,
    ArabicAssigneeFixture Youssef,
    ArabicAssigneeFixture Nour,
    ArabicAssigneeFixture Mahmoud,
    ArabicAssigneeFixture Mohamed,
    ArabicAssigneeFixture Karim,
    ArabicAssigneeFixture Fatima,
    ArabicAssigneeFixture Omar);
