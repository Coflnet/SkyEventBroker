using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyBase.Migrations
{
    /// <inheritdoc />
    public partial class SeparateSubId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceSubId",
                table: "Subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceSubId",
                table: "Subscriptions");
        }
    }
}
