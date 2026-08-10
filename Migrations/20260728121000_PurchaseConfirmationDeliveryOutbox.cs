using System;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SkyBase.Migrations;

[DbContext(typeof(EventDbContext))]
[Migration("20260728121000_PurchaseConfirmationDeliveryOutbox")]
public partial class PurchaseConfirmationDeliveryOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PurchaseConfirmationDeliveries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Reference = table.Column<string>(
                    type: "character varying(32)",
                    maxLength: 32,
                    nullable: false),
                Recipient = table.Column<string>(
                    type: "character varying(320)",
                    maxLength: 320,
                    nullable: true),
                Locale = table.Column<string>(
                    type: "character varying(16)",
                    maxLength: 16,
                    nullable: true),
                Payload = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),
                NextAttemptAt = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),
                SentAt = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                LeaseUntil = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true),
                LastError = table.Column<string>(
                    type: "character varying(2000)",
                    maxLength: 2000,
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_PurchaseConfirmationDeliveries",
                    x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseConfirmationDeliveries_Reference",
            table: "PurchaseConfirmationDeliveries",
            column: "Reference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseConfirmationDeliveries_SentAt_NextAttemptAt",
            table: "PurchaseConfirmationDeliveries",
            columns: new[] { "SentAt", "NextAttemptAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PurchaseConfirmationDeliveries");
    }
}
