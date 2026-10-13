using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Removes NUL characters from the text that is about to be saved.
/// </summary>
/// <remarks>
/// PostgreSQL text cannot hold NUL characters and rejects the whole save over a single one. They turn up in embedded
/// metadata such as tags written by broken taggers, and SQLite stored them without complaint, so one bad file would
/// otherwise stop a library scan from saving anything.
/// </remarks>
internal sealed class NulCharacterSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RemoveNulCharacters(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RemoveNulCharacters(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void RemoveNulCharacters(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is string value && value.Contains('\0', StringComparison.Ordinal))
                {
                    property.CurrentValue = value.Replace("\0", string.Empty, StringComparison.Ordinal);
                }
            }
        }
    }
}
