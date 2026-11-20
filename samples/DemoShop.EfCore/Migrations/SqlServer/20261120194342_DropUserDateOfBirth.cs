using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoShop.EfCore.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class DropUserDateOfBirth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "date_of_birth",
                schema: "shop",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "date_of_birth",
                schema: "shop",
                table: "users",
                type: "date",
                nullable: true);
        }
    }
}
