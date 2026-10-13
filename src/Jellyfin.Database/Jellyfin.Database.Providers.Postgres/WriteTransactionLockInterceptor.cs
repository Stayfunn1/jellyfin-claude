using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Lets only one transaction at a time change the database, the way SQLite does.
/// </summary>
/// <remarks>
/// SQLite transactions are opened with BEGIN IMMEDIATE, which takes the single write lock of the database before the
/// first statement. Jellyfin relies on that throughout: it reads whether rows exist and then inserts the missing ones,
/// e.g. items, genres and people found by a library scan. Under PostgreSQL's READ COMMITTED two scans saving the same
/// new item or genre both find it missing, and the second insert fails on the unique key. Each transaction therefore
/// first takes a transaction-scoped advisory lock, which PostgreSQL releases on commit or rollback. Reads, and writes
/// outside a transaction, are not held up.
/// </remarks>
internal sealed class WriteTransactionLockInterceptor : DbTransactionInterceptor
{
    /// <summary>
    /// How long a transaction waits for the one in progress, in seconds. Matches the busy timeout of the SQLite provider.
    /// </summary>
    private const int LockTimeoutSeconds = 60;

    /// <summary>
    /// Takes the lock. The key is the ASCII bytes of "JELLYFIN" read as a number; advisory locks are local to the database.
    /// </summary>
    private const string LockSql = "SELECT pg_advisory_xact_lock(5351767622998706510)";

    /// <inheritdoc />
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateLockCommand(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        var command = CreateLockCommand(connection, result);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static DbCommand CreateLockCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LockSql;
        command.CommandTimeout = LockTimeoutSeconds;
        return command;
    }
}
