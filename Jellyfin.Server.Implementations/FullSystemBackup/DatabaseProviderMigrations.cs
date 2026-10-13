using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Server.Implementations.Extensions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Knows the schema migrations of the database providers that ship with jellyfin.
/// </summary>
internal static class DatabaseProviderMigrations
{
    private static readonly Lazy<IReadOnlySet<string>> _migrationIds = new(LoadMigrationIds);

    /// <summary>
    /// Gets the ids of the schema migrations of every built-in database provider.
    /// </summary>
    /// <returns>The migration ids.</returns>
    public static IReadOnlySet<string> GetMigrationIds() => _migrationIds.Value;

    private static IReadOnlySet<string> LoadMigrationIds()
    {
        return ServiceCollectionExtensions.DatabaseProviderTypes()
            .Select(e => e.Assembly)
            .Distinct()
            .SelectMany(e => e.GetTypes())
            .Where(e => e.IsSubclassOf(typeof(Migration)))
            .Select(e => e.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
