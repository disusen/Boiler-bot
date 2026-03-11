using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductivityBot.Migrations
{
    /// <inheritdoc />
    public partial class CompanionMemoryLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CurrentMood = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CurrentThought = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RecentObservation = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ConsecutiveGoodDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveRoughDays = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Facts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Confidence = table.Column<float>(type: "REAL", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserProvided = table.Column<bool>(type: "INTEGER", nullable: false),
                    FactKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Goals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SurfaceAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Goals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Valence = table.Column<int>(type: "INTEGER", nullable: false),
                    Importance = table.Column<float>(type: "REAL", nullable: false),
                    TimesReferenced = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReferencedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotStates_UserId",
                table: "BotStates",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Facts_UserId_Category",
                table: "Facts",
                columns: new[] { "UserId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_Facts_UserId_FactKey",
                table: "Facts",
                columns: new[] { "UserId", "FactKey" },
                unique: true,
                filter: "[FactKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Goals_UserId_Status",
                table: "Goals",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_UserId_Importance_OccurredAt",
                table: "Memories",
                columns: new[] { "UserId", "Importance", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_UserId_Tag",
                table: "Memories",
                columns: new[] { "UserId", "Tag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotStates");

            migrationBuilder.DropTable(
                name: "Facts");

            migrationBuilder.DropTable(
                name: "Goals");

            migrationBuilder.DropTable(
                name: "Memories");
        }
    }
}
