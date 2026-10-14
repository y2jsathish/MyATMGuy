using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ATM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reversals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Terminal = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAttemptUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ResolvedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reversals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reversals_CreatedUtc",
                table: "Reversals",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Reversals_State_NextAttemptUtc",
                table: "Reversals",
                columns: new[] { "State", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reversals");
        }
    }
}
