using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductivityBot.Migrations
{
    /// <inheritdoc />
    public partial class BeliefSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UserCreated",
                table: "Goals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Beliefs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    BeliefKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Confidence = table.Column<float>(type: "REAL", nullable: false),
                    Domain = table.Column<int>(type: "INTEGER", nullable: false),
                    BehavioralImplication = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConfirmationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContradictionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FormationEvidence = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FormedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastEvidenceAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Beliefs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Beliefs_UserId_BeliefKey",
                table: "Beliefs",
                columns: new[] { "UserId", "BeliefKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Beliefs_UserId_Status",
                table: "Beliefs",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Beliefs");

            migrationBuilder.DropColumn(
                name: "UserCreated",
                table: "Goals");
        }
    }
}
