using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SkyBase.Migrations
{
    /// <inheritdoc />
    public partial class MessageSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "Timestamp",
                table: "Messages",
                type: "timestamp with time zone",
                rowVersion: true,
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldRowVersion: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageLink",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduledMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    ScheduledTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledMessages_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_MessageId",
                table: "ScheduledMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_ScheduledTime",
                table: "ScheduledMessages",
                column: "ScheduledTime");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMessages_UserId",
                table: "ScheduledMessages",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledMessages");

            migrationBuilder.DropColumn(
                name: "ImageLink",
                table: "Messages");

            migrationBuilder.AlterColumn<DateTime>(
                name: "Timestamp",
                table: "Messages",
                type: "timestamp with time zone",
                rowVersion: true,
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldRowVersion: true,
                oldNullable: true);
        }
    }
}
