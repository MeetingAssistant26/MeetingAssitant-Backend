using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddPostMeetingProcessingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostMeetingProcessingRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineGenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RelatedHangfireJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMeetingProcessingRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostMeetingProcessingRuns_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostMeetingProcessingSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RelatedHangfireJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ArtifactType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactIdsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMeetingProcessingSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostMeetingProcessingSteps_PostMeetingProcessingRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PostMeetingProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostMeetingProcessingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepType = table.Column<int>(type: "integer", nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RelatedHangfireJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ArtifactType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactIdsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMeetingProcessingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostMeetingProcessingEvents_PostMeetingProcessingRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PostMeetingProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostMeetingProcessingEvents_PostMeetingProcessingSteps_Step~",
                        column: x => x.StepId,
                        principalTable: "PostMeetingProcessingSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingEvents_Org_Meeting_OccurredAt",
                table: "PostMeetingProcessingEvents",
                columns: new[] { "OrganizationId", "MeetingId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingEvents_Run_OccurredAt",
                table: "PostMeetingProcessingEvents",
                columns: new[] { "RunId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingEvents_StepId",
                table: "PostMeetingProcessingEvents",
                column: "StepId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingRuns_MeetingId",
                table: "PostMeetingProcessingRuns",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingRuns_Org_Meeting_CreatedAt",
                table: "PostMeetingProcessingRuns",
                columns: new[] { "OrganizationId", "MeetingId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingRuns_Org_Status",
                table: "PostMeetingProcessingRuns",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_PostMeetingProcessingRuns_Org_Meeting_Generation",
                table: "PostMeetingProcessingRuns",
                columns: new[] { "OrganizationId", "MeetingId", "PipelineGenerationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingSteps_Org_Meeting_Status",
                table: "PostMeetingProcessingSteps",
                columns: new[] { "OrganizationId", "MeetingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PostMeetingProcessingSteps_Org_Meeting_StepType",
                table: "PostMeetingProcessingSteps",
                columns: new[] { "OrganizationId", "MeetingId", "StepType" });

            migrationBuilder.CreateIndex(
                name: "UX_PostMeetingProcessingSteps_Run_StepType",
                table: "PostMeetingProcessingSteps",
                columns: new[] { "RunId", "StepType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostMeetingProcessingEvents");

            migrationBuilder.DropTable(
                name: "PostMeetingProcessingSteps");

            migrationBuilder.DropTable(
                name: "PostMeetingProcessingRuns");
        }
    }
}
