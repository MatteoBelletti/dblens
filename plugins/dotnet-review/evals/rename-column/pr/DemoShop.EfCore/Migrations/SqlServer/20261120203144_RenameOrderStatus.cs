using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoShop.EfCore.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class RenameOrderStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "status",
                schema: "shop",
                table: "orders",
                newName: "state");

            migrationBuilder.RenameIndex(
                name: "ix_orders_status",
                schema: "shop",
                table: "orders",
                newName: "ix_orders_state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "state",
                schema: "shop",
                table: "orders",
                newName: "status");

            migrationBuilder.RenameIndex(
                name: "ix_orders_state",
                schema: "shop",
                table: "orders",
                newName: "ix_orders_status");
        }
    }
}
