using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "PendingActivation"),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    HomeAddress = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    HomeLatitude = table.Column<double>(type: "float", nullable: true),
                    HomeLongitude = table.Column<double>(type: "float", nullable: true),
                    CommuteDistanceKm = table.Column<double>(type: "float", nullable: true),
                    HomeVerified = table.Column<bool>(type: "bit", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollusionFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserA = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserB = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MutualInteractions = table.Column<int>(type: "int", nullable: false),
                    ConcentrationAPercent = table.Column<int>(type: "int", nullable: false),
                    ConcentrationBPercent = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollusionFlags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Muted = table.Column<bool>(type: "bit", nullable: false),
                    MutedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Settings = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Descriptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Resources = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParkerScores",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    LastCreditGrantPeriod = table.Column<int>(type: "int", nullable: false),
                    ReservationsCompleted = table.Column<int>(type: "int", nullable: false),
                    ReservationsReleased = table.Column<int>(type: "int", nullable: false),
                    OffPeakReservations = table.Column<int>(type: "int", nullable: false),
                    NoShows = table.Column<int>(type: "int", nullable: false),
                    CompletionStreak = table.Column<int>(type: "int", nullable: false),
                    QueueBannedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAllowancePenalty = table.Column<int>(type: "int", nullable: false),
                    LastDecayUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TrustScore = table.Column<int>(type: "int", nullable: false),
                    TrustComputedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkerScores", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ParkingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReleasePoints = table.Column<int>(type: "int", nullable: false),
                    OffPeakBonusPoints = table.Column<int>(type: "int", nullable: false),
                    NoShowPenaltyPoints = table.Column<int>(type: "int", nullable: false),
                    ReleaseCutoff = table.Column<TimeSpan>(type: "time", nullable: false),
                    NoShowGracePeriod = table.Column<TimeSpan>(type: "time", nullable: false),
                    ReminderLeadTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    PeakStart = table.Column<TimeOnly>(type: "time", nullable: false),
                    PeakEnd = table.Column<TimeOnly>(type: "time", nullable: false),
                    SweepInterval = table.Column<TimeSpan>(type: "time", nullable: false),
                    ResidentHoldUntil = table.Column<TimeOnly>(type: "time", nullable: false),
                    ResidentReleasePointsPerHour = table.Column<int>(type: "int", nullable: false),
                    ResidentReleaseMaxPoints = table.Column<int>(type: "int", nullable: false),
                    ResidentMaxShareAllowance = table.Column<int>(type: "int", nullable: false),
                    ResidentSharePercentPerAllowance = table.Column<int>(type: "int", nullable: false),
                    ResidentWastedShareClawbackPercent = table.Column<int>(type: "int", nullable: false),
                    LotLatitude = table.Column<double>(type: "float", nullable: true),
                    LotLongitude = table.Column<double>(type: "float", nullable: true),
                    SharedTakenBasePoints = table.Column<int>(type: "int", nullable: false),
                    SharedTakenReferenceKm = table.Column<int>(type: "int", nullable: false),
                    SharedTakenMaxMultiplier = table.Column<int>(type: "int", nullable: false),
                    AutoVerifyHomeAddress = table.Column<bool>(type: "bit", nullable: false),
                    AutoVerifyMaxDistanceKm = table.Column<int>(type: "int", nullable: false),
                    MaxRewardedReleasesPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxReleaseRangeDays = table.Column<int>(type: "int", nullable: false),
                    BaseReservationCost = table.Column<int>(type: "int", nullable: false, defaultValue: 10),
                    PeakPricePercent = table.Column<int>(type: "int", nullable: false, defaultValue: 200),
                    OccupancyPricePercent = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    MaxReservationCost = table.Column<int>(type: "int", nullable: false, defaultValue: 40),
                    MonthlyCreditAllowance = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    QueueOfferMinutes = table.Column<int>(type: "int", nullable: false, defaultValue: 15),
                    QueueNoShowPenaltyPoints = table.Column<int>(type: "int", nullable: false, defaultValue: 50),
                    QueueNoShowCreditPenalty = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    QueueNoShowBanDays = table.Column<int>(type: "int", nullable: false, defaultValue: 14),
                    QueueNoShowAllowancePenalty = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    DemandReleaseOccupancyPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    DemandReleaseQueueBonus = table.Column<int>(type: "int", nullable: false, defaultValue: 5),
                    MaxReleaseReward = table.Column<int>(type: "int", nullable: false, defaultValue: 40),
                    StreakBonusPerLevel = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    StreakBonusCap = table.Column<int>(type: "int", nullable: false, defaultValue: 20),
                    TierSilverPoints = table.Column<int>(type: "int", nullable: false, defaultValue: 50),
                    TierGoldPoints = table.Column<int>(type: "int", nullable: false, defaultValue: 150),
                    TierPlatinumPoints = table.Column<int>(type: "int", nullable: false, defaultValue: 300),
                    QueuePriorityPerTier = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    TierAllowanceBonus = table.Column<int>(type: "int", nullable: false, defaultValue: 20),
                    TierDiscountPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 5),
                    ReputationDecayPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 10),
                    ReputationDecayIntervalDays = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    AdaptivePricingEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    AdaptiveTargetOccupancyPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 85),
                    AdaptiveGainPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    AdaptiveDeadbandPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 5),
                    AdaptiveStepMaxPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 25),
                    AdaptivePeakMinPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    AdaptivePeakMaxPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 400),
                    AdaptiveIntervalMinutes = table.Column<int>(type: "int", nullable: false, defaultValue: 60),
                    LastAdaptiveAdjustUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TrustEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    TrustIntervalHours = table.Column<int>(type: "int", nullable: false, defaultValue: 24),
                    TrustedBadgeThreshold = table.Column<int>(type: "int", nullable: false, defaultValue: 60),
                    LastTrustComputeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MaxPairTrustWeight = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    AntiCollusionEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CollusionMinInteractions = table.Column<int>(type: "int", nullable: false, defaultValue: 4),
                    CollusionConcentrationPercent = table.Column<int>(type: "int", nullable: false, defaultValue: 70),
                    CollusionScanIntervalHours = table.Column<int>(type: "int", nullable: false, defaultValue: 24),
                    LastCollusionScanUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParkingSpots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MonthlyShareAllowance = table.Column<int>(type: "int", nullable: false),
                    LastResidentReminderDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastAutoShareNoticeDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingSpots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PointsLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OfferedSpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OfferExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsOffPeak = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CheckedInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReminderSentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreditsCharged = table.Column<int>(type: "int", nullable: false),
                    FromQueue = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalHost = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    Scheme = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: true),
                    ForceHttps = table.Column<bool>(type: "bit", nullable: false),
                    HstsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    HstsMaxAgeDays = table.Column<int>(type: "int", nullable: false),
                    HstsIncludeSubDomains = table.Column<bool>(type: "bit", nullable: false),
                    HstsPreload = table.Column<bool>(type: "bit", nullable: false),
                    WwwPreference = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Aliases = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefaultLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    DefaultTimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PageCharset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    EmailCharset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    DefaultRole = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LowercaseUrls = table.Column<bool>(type: "bit", nullable: false),
                    TrailingSlash = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SiteName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SiteDescription = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpotReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AwardedPoints = table.Column<int>(type: "int", nullable: false),
                    ReconciledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserBadges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Badge = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AwardedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBadges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AuthorizationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAuditEvents_UserId_OccurredAtUtc",
                table: "AccountAuditEvents",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CollusionFlags_Status",
                table: "CollusionFlags",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CollusionFlags_UserA_UserB",
                table: "CollusionFlags",
                columns: new[] { "UserA", "UserB" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true,
                filter: "[ReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ParkerScores_Points",
                table: "ParkerScores",
                column: "Points");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpots_Code",
                table: "ParkingSpots",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_UserId_OccurredAtUtc",
                table: "PointsLedgerEntries",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueEntries_Status_CreatedAtUtc",
                table: "QueueEntries",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueEntries_UserId_Status",
                table: "QueueEntries",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_SpotId_StartUtc",
                table: "Reservations",
                columns: new[] { "SpotId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Status",
                table: "Reservations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_UserId_StartUtc",
                table: "Reservations",
                columns: new[] { "UserId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotReleases_OwnerId_Date",
                table: "SpotReleases",
                columns: new[] { "OwnerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotReleases_SpotId_Date",
                table: "SpotReleases",
                columns: new[] { "SpotId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserBadges_UserId_Badge",
                table: "UserBadges",
                columns: new[] { "UserId", "Badge" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAuditEvents");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "CollusionFlags");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "ParkerScores");

            migrationBuilder.DropTable(
                name: "ParkingSettings");

            migrationBuilder.DropTable(
                name: "ParkingSpots");

            migrationBuilder.DropTable(
                name: "PointsLedgerEntries");

            migrationBuilder.DropTable(
                name: "QueueEntries");

            migrationBuilder.DropTable(
                name: "Reservations");

            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "SpotReleases");

            migrationBuilder.DropTable(
                name: "UserBadges");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");
        }
    }
}
