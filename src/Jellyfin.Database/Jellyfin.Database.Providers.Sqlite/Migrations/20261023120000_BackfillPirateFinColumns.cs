using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations;

/// <summary>
/// Items created before the PirateFin columns existed only carry the flags inside their Data json. Copying them
/// into the columns lets queries use the columns instead of scanning Data with LIKE.
/// </summary>
[DbContext(typeof(JellyfinDbContext))]
[Migration("20261023120000_BackfillPirateFinColumns")]
public partial class BackfillPirateFinColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""UPDATE BaseItems SET IsInternet = 1 WHERE IsInternet = 0 AND Data LIKE '%"IsInternet":true%';""");
        migrationBuilder.Sql("""UPDATE BaseItems SET Scheduled = 1 WHERE Scheduled = 0 AND Data LIKE '%"Scheduled":true%';""");
        migrationBuilder.Sql("""UPDATE BaseItems SET IsDownloaded = 1 WHERE IsDownloaded = 0 AND Data LIKE '%"IsDownloaded":true%';""");
        migrationBuilder.Sql("""UPDATE BaseItems SET Subscribed = 1 WHERE Subscribed = 0 AND Data LIKE '%"Subscribed":true%';""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
