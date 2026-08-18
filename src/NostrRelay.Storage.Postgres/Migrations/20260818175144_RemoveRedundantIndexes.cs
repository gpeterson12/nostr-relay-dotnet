using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NostrRelay.Storage.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundantIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_pubkey_kind_dtag",
                table: "events");

            migrationBuilder.DropIndex(
                name: "idx_events_tags_gin",
                table: "events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_events_pubkey_kind_dtag",
                table: "events",
                columns: new[] { "pubkey", "kind", "d_tag" });

            migrationBuilder.CreateIndex(
                name: "idx_events_tags_gin",
                table: "events",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");
        }
    }
}
