using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoShop.EfCore.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class RemoveProductStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stock",
                schema: "shop",
                table: "products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "stock",
                schema: "shop",
                table: "products",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
