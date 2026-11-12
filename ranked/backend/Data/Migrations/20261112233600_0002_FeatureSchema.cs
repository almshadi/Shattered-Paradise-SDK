using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ranked.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0002_FeatureSchema : Migration
    {
        /// <inheritdoc />
        // Defaults of the new non-nullable columns were hand-edited so existing rows get sensible values
        // (Deviation 350, Volatility 0.06, QueueType "1v1", TeamSize 1). The model itself has no DB defaults.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BanReason",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BannedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordId",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DodgeCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDodgeAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QueueBlockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Deviation",
                table: "Ratings",
                type: "double precision",
                nullable: false,
                defaultValue: 350.0);

            migrationBuilder.AddColumn<int>(
                name: "SeasonId",
                table: "Ratings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Volatility",
                table: "Ratings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.06);

            migrationBuilder.AddColumn<string>(
                name: "MapVetoes",
                table: "Queue",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueType",
                table: "Queue",
                type: "text",
                nullable: false,
                defaultValue: "1v1");

            migrationBuilder.AddColumn<double>(
                name: "DeviationAfter",
                table: "MatchPlayers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviationBefore",
                table: "MatchPlayers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Placement",
                table: "MatchPlayers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Team",
                table: "MatchPlayers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "QueueType",
                table: "Matches",
                type: "text",
                nullable: false,
                defaultValue: "1v1");

            migrationBuilder.AddColumn<string>(
                name: "ReplayFile",
                table: "Matches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonId",
                table: "Matches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamSize",
                table: "Matches",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AdminActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetMatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Seasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeasonStandings",
                columns: table => new
                {
                    SeasonId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Mmr = table.Column<double>(type: "double precision", nullable: false),
                    Games = table.Column<int>(type: "integer", nullable: false),
                    Wins = table.Column<int>(type: "integer", nullable: false),
                    Losses = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonStandings", x => new { x.SeasonId, x.UserId });
                    table.ForeignKey(
                        name: "FK_SeasonStandings_Seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeasonStandings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_DiscordId",
                table: "Users",
                column: "DiscordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_SeasonId",
                table: "Ratings",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_SeasonId",
                table: "Matches",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminActions_CreatedAt",
                table: "AdminActions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminActions_TargetUserId",
                table: "AdminActions",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonStandings_UserId",
                table: "SeasonStandings",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Matches_Seasons_SeasonId",
                table: "Matches",
                column: "SeasonId",
                principalTable: "Seasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Ratings_Seasons_SeasonId",
                table: "Ratings",
                column: "SeasonId",
                principalTable: "Seasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Matches_Seasons_SeasonId",
                table: "Matches");

            migrationBuilder.DropForeignKey(
                name: "FK_Ratings_Seasons_SeasonId",
                table: "Ratings");

            migrationBuilder.DropTable(
                name: "AdminActions");

            migrationBuilder.DropTable(
                name: "SeasonStandings");

            migrationBuilder.DropTable(
                name: "Seasons");

            migrationBuilder.DropIndex(
                name: "IX_Users_DiscordId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Ratings_SeasonId",
                table: "Ratings");

            migrationBuilder.DropIndex(
                name: "IX_Matches_SeasonId",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "BanReason",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BannedUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DodgeCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastDodgeAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QueueBlockedUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Deviation",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "Volatility",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "MapVetoes",
                table: "Queue");

            migrationBuilder.DropColumn(
                name: "QueueType",
                table: "Queue");

            migrationBuilder.DropColumn(
                name: "DeviationAfter",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "DeviationBefore",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "Placement",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "Team",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "QueueType",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "ReplayFile",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "TeamSize",
                table: "Matches");
        }
    }
}
