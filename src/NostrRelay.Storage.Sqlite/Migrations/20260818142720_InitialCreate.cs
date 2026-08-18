using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NostrRelay.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    pubkey = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    tags = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    sig = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    d_tag = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "event_tags",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    tag_name = table.Column<string>(type: "TEXT", nullable: false),
                    tag_value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_event_tags_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_event_tags_event_id",
                table: "event_tags",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "idx_event_tags_name_value",
                table: "event_tags",
                columns: new[] { "tag_name", "tag_value" });

            migrationBuilder.CreateIndex(
                name: "idx_events_created_at",
                table: "events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "idx_events_expires_at",
                table: "events",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "idx_events_kind_created_at",
                table: "events",
                columns: new[] { "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_events_pubkey_kind",
                table: "events",
                columns: new[] { "pubkey", "kind" });

            migrationBuilder.CreateIndex(
                name: "idx_events_pubkey_kind_dtag",
                table: "events",
                columns: new[] { "pubkey", "kind", "d_tag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_tags");

            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
