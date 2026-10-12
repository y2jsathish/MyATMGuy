using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ATM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Terminal = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "Configuration",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configuration", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Terminal = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Errors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Errors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Terminal = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Card = table.Column<string>(type: "TEXT", maxLength: 19, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CorrelationId",
                table: "AuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Id",
                table: "AuditLogs",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Terminal",
                table: "AuditLogs",
                column: "Terminal");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Terminal_Kind",
                table: "Devices",
                columns: new[] { "Terminal", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Errors_CorrelationId",
                table: "Errors",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Errors_TimestampUtc",
                table: "Errors",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CreatedUtc",
                table: "Transactions",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Status",
                table: "Transactions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Terminal",
                table: "Transactions",
                column: "Terminal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Configuration");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Errors");

            migrationBuilder.DropTable(
                name: "Transactions");
        }
    }
}
