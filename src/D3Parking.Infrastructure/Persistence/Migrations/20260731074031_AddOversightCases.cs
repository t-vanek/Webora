using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Turns the two review queues into one desk of cases: a case per signal, with an owner, a
    /// verdict and an append-only history.
    /// </summary>
    /// <remarks>
    /// The backfill is not a convenience — it is what makes dropping <c>CollusionFlags.Status</c>
    /// safe. That column carried a decision ("dismissed as a false positive") that the nightly scan
    /// reads to leave a pair alone, so the cases have to be seeded with it before the column goes.
    /// Everything history had already dealt with is seeded closed, so the first queue a reviewer
    /// opens holds the work that is actually outstanding rather than every report ever filed.
    /// </remarks>
    public partial class AddOversightCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence<int>(
                name: "OversightCaseNumbers");

            migrationBuilder.CreateTable(
                name: "OversightCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false, defaultValueSql: "NEXT VALUE FOR [OversightCaseNumbers]"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReporterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OversightCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OversightCaseEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OversightCaseEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OversightCaseEvents_OversightCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "OversightCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OversightCaseEvents_CaseId_OccurredAtUtc",
                table: "OversightCaseEvents",
                columns: new[] { "CaseId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_AssigneeId_Status",
                table: "OversightCases",
                columns: new[] { "AssigneeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_Kind_SubjectId",
                table: "OversightCases",
                columns: new[] { "Kind", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_Number",
                table: "OversightCases",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_SpotId",
                table: "OversightCases",
                column: "SpotId");

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_Status_UpdatedAtUtc",
                table: "OversightCases",
                columns: new[] { "Status", "UpdatedAtUtc" });

            BackfillCases(migrationBuilder);

            // Only now that the decision it carried lives on the cases.
            migrationBuilder.DropIndex(
                name: "IX_CollusionFlags_Status",
                table: "CollusionFlags");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "CollusionFlags");
        }

        /// <summary>
        /// Opens a case for every signal already on record, dated to when the signal was raised and
        /// carrying whatever verdict history had already reached:
        /// <list type="bullet">
        /// <item>a report whose apology voucher was approved or rejected is closed as founded or
        /// unfounded — that ruling <em>was</em> the verdict on the report;</item>
        /// <item>a report that never granted a voucher is closed as needing no action: the old page
        /// listed it as a trend row, never as something waiting on a decision;</item>
        /// <item>a report with a voucher still pending stays open — it is the outstanding work;</item>
        /// <item>a dismissed pair is closed as a false positive, which is what keeps the nightly
        /// scan from raising it again, and a reviewed pair is closed as founded.</item>
        /// </list>
        /// Numbers are handed out in the order the signals were raised, so case 1 is the oldest.
        /// </summary>
        private static void BackfillCases(MigrationBuilder migrationBuilder)
        {
            const string migratedNote = "Převedeno při zavedení případů provozního dohledu.";

            migrationBuilder.Sql($"""
                INSERT INTO [OversightCases]
                    ([Id], [Number], [Kind], [SubjectId], [SpotId], [ReporterId], [Status], [Priority],
                     [AssigneeId], [OpenedAtUtc], [UpdatedAtUtc], [ResolvedAtUtc], [Resolution], [ResolutionNote])
                SELECT
                    NEWID(),
                    NEXT VALUE FOR [OversightCaseNumbers] OVER (ORDER BY m.[ReportedAtUtc]),
                    'OccupancyMismatch',
                    m.[Id],
                    m.[SpotId],
                    m.[ReporterId],
                    CASE WHEN v.[Status] = 'PendingApproval' THEN 'New' ELSE 'Resolved' END,
                    'Normal',
                    v.[ReviewedById],
                    m.[ReportedAtUtc],
                    m.[ReportedAtUtc],
                    CASE WHEN v.[Status] IN ('Approved', 'Rejected') THEN v.[ReviewedAtUtc] END,
                    CASE v.[Status]
                        WHEN 'Approved' THEN 'Founded'
                        WHEN 'Rejected' THEN 'Unfounded'
                        WHEN 'PendingApproval' THEN NULL
                        ELSE 'NoActionNeeded'
                    END,
                    CASE WHEN v.[Status] = 'PendingApproval' THEN NULL ELSE N'{migratedNote}' END
                FROM [OccupancyMismatches] m
                LEFT JOIN [ApologyVouchers] v ON v.[SourceMismatchId] = m.[Id];
                """);

            migrationBuilder.Sql($"""
                INSERT INTO [OversightCases]
                    ([Id], [Number], [Kind], [SubjectId], [SpotId], [ReporterId], [Status], [Priority],
                     [AssigneeId], [OpenedAtUtc], [UpdatedAtUtc], [ResolvedAtUtc], [Resolution], [ResolutionNote])
                SELECT
                    NEWID(),
                    NEXT VALUE FOR [OversightCaseNumbers] OVER (ORDER BY f.[DetectedAtUtc]),
                    'CollusionRing',
                    f.[Id],
                    NULL,
                    NULL,
                    CASE WHEN f.[Status] = 'New' THEN 'New' ELSE 'Resolved' END,
                    'Normal',
                    NULL,
                    f.[DetectedAtUtc],
                    f.[UpdatedAtUtc],
                    CASE WHEN f.[Status] <> 'New' THEN f.[UpdatedAtUtc] END,
                    CASE f.[Status]
                        WHEN 'Reviewed' THEN 'Founded'
                        WHEN 'Dismissed' THEN 'Unfounded'
                        ELSE NULL
                    END,
                    CASE WHEN f.[Status] = 'New' THEN NULL ELSE N'{migratedNote}' END
                FROM [CollusionFlags] f;
                """);

            // Every case opens with the line that says so; the ones seeded closed get the ruling
            // too, so a migrated case reads like any other rather than like an empty history. The
            // two statements run in this order, which is the order the ordinals come out in.
            migrationBuilder.Sql("""
                INSERT INTO [OversightCaseEvents]
                    ([Id], [CaseId], [Type], [Actor], [ActorUserId], [Body], [Visibility], [OccurredAtUtc])
                SELECT NEWID(), c.[Id], 'Opened', 'System', NULL, NULL, 'Internal', c.[OpenedAtUtc]
                FROM [OversightCases] c;
                """);

            migrationBuilder.Sql($"""
                INSERT INTO [OversightCaseEvents]
                    ([Id], [CaseId], [Type], [Actor], [ActorUserId], [Body], [Visibility], [OccurredAtUtc])
                SELECT NEWID(), c.[Id], 'Resolved', 'System', NULL, N'{migratedNote}', 'Internal',
                       ISNULL(c.[ResolvedAtUtc], c.[UpdatedAtUtc])
                FROM [OversightCases] c
                WHERE c.[Status] = 'Resolved';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The flags take their verdict back from the cases before those are dropped.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CollusionFlags",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.Sql("""
                UPDATE f SET [Status] =
                    CASE c.[Resolution]
                        WHEN 'Unfounded' THEN 'Dismissed'
                        WHEN 'Founded' THEN 'Reviewed'
                        ELSE 'New'
                    END
                FROM [CollusionFlags] f
                JOIN [OversightCases] c ON c.[SubjectId] = f.[Id] AND c.[Kind] = 'CollusionRing';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CollusionFlags_Status",
                table: "CollusionFlags",
                column: "Status");

            migrationBuilder.DropTable(
                name: "OversightCaseEvents");

            migrationBuilder.DropTable(
                name: "OversightCases");

            migrationBuilder.DropSequence(
                name: "OversightCaseNumbers");
        }
    }
}
