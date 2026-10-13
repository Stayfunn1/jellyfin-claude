using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Builds the indexes in the NULL order the queries sort in, see <see cref="NullsFirstQuerySqlGeneratorFactory"/>.
/// </summary>
/// <remarks>
/// A B-tree index only returns rows already sorted for an ORDER BY whose NULL placement matches its own, read in either
/// direction. PostgreSQL builds indexes with NULL last, which matches neither direction of the NULL-first order the
/// queries use, so sorted queries such as the latest additions would have to sort the whole table instead.
/// </remarks>
internal sealed class NullsFirstIndexConvention : IModelFinalizingConvention
{
    /// <inheritdoc/>
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var index in entityType.GetDeclaredIndexes())
            {
                if (!index.Properties.Any(p => p.IsNullable))
                {
                    continue;
                }

                var descending = index.IsDescending;
                var nullSortOrder = index.Properties
                    .Select((property, i) => !property.IsNullable
                        ? NullSortOrder.Unspecified
                        : descending is not null && (descending.Count == 0 || descending[i])
                            ? NullSortOrder.NullsLast
                            : NullSortOrder.NullsFirst)
                    .ToArray();

                index.SetNullSortOrder(nullSortOrder);
            }
        }
    }
}
