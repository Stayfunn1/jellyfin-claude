using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Creates SQL generators that sort NULL the way SQLite does.
/// </summary>
/// <remarks>
/// SQLite treats NULL as smaller than any value: first in ascending order, last in descending order. PostgreSQL does
/// the opposite, so every sort on an optional value, e.g. the date an item was last played, its premiere date or its
/// rating, would put the items without one at the other end of the list. Npgsql can write the SQLite order, but only
/// exposes the switch to its own tests.
/// </remarks>
#pragma warning disable EF1001 // Internal EF Core API usage. Npgsql's generator has no public way to set this.
internal sealed class NullsFirstQuerySqlGeneratorFactory : IQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly INpgsqlSingletonOptions _npgsqlOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="NullsFirstQuerySqlGeneratorFactory"/> class.
    /// </summary>
    /// <param name="dependencies">The SQL generator dependencies.</param>
    /// <param name="typeMappingSource">The type mapping source.</param>
    /// <param name="npgsqlOptions">The Npgsql options.</param>
    public NullsFirstQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        INpgsqlSingletonOptions npgsqlOptions)
    {
        _dependencies = dependencies;
        _typeMappingSource = typeMappingSource;
        _npgsqlOptions = npgsqlOptions;
    }

    /// <inheritdoc />
    public QuerySqlGenerator Create()
        => new NpgsqlQuerySqlGenerator(_dependencies, _typeMappingSource, reverseNullOrderingEnabled: true, _npgsqlOptions.PostgresVersion);
}
#pragma warning restore EF1001
