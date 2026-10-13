using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Drops keys that no current property reads or writes from the serialized <c>BaseItems.Data</c> blob.
/// </summary>
[JellyfinMigration("2026-09-11T12:00:00", nameof(StripEmbeddedLinkedChildren))]
internal class StripEmbeddedLinkedChildren : IDatabaseMigrationRoutine
{
    private readonly ILogger<StripEmbeddedLinkedChildren> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    public StripEmbeddedLinkedChildren(
        ILoggerFactory loggerFactory,
        IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _logger = loggerFactory.CreateLogger<StripEmbeddedLinkedChildren>();
        _dbProvider = dbProvider;
    }

    /// <inheritdoc/>
    public void Perform()
    {
        using var context = _dbProvider.CreateDbContext();

        // The validity check guards the rare malformed blob: removing keys from it would abort the
        // statement, and one bad row must not cost every other row the fix.
        var updated = context.Database.ExecuteSqlRaw(context.Database.IsNpgsql()
            ? """
                UPDATE "BaseItems"
                SET "Data" = ("Data"::jsonb - 'LinkedChildren' - 'ExtraIds' - 'SupportsExternalTransfer')::text
                WHERE "Data" IS NOT NULL
                  AND pg_input_is_valid("Data", 'jsonb')
                  AND ("Data" LIKE '%"LinkedChildren"%'
                    OR "Data" LIKE '%"ExtraIds"%'
                    OR "Data" LIKE '%"SupportsExternalTransfer"%')
                """
            : """
                UPDATE "BaseItems"
                SET "Data" = json_remove("Data", '$.LinkedChildren', '$.ExtraIds', '$.SupportsExternalTransfer')
                WHERE "Data" IS NOT NULL
                  AND json_valid("Data") = 1
                  AND ("Data" LIKE '%"LinkedChildren"%'
                    OR "Data" LIKE '%"ExtraIds"%'
                    OR "Data" LIKE '%"SupportsExternalTransfer"%')
                """);

        _logger.LogInformation("Dropped dead keys from the serialized data of {Count} items", updated);
    }
}
