using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations;

[DbContext(typeof(JellyfinDbContext))]
[Migration("20261012130000_AddEpisodeScheduleProperties")]
public partial class AddEpisodeScheduleProperties : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDownloaded",
            table: "BaseItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsInternet",
            table: "BaseItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "Scheduled",
            table: "BaseItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsDownloaded",
            table: "BaseItems");

        migrationBuilder.DropColumn(
            name: "IsInternet",
            table: "BaseItems");

        migrationBuilder.DropColumn(
            name: "Scheduled",
            table: "BaseItems");
    }
}
