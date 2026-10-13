# Running Jellyfin on PostgreSQL

Jellyfin keeps its library, users, watch history and settings in a database. Out of the box that is a SQLite file
(`jellyfin.db` in the data directory). This build can use a PostgreSQL server instead.

- SQLite stays the default. Nothing changes for a server until you point it at PostgreSQL.
- The PostgreSQL provider is tested against **PostgreSQL 18** (18.6). The automated tests also pass on PostgreSQL 16.
- Media files, images, metadata, trickplay and subtitles stay on disk where they are. Only the database moves.

## 1. Prepare the database server

Create a login role for Jellyfin, and a database it owns:

```sql
CREATE ROLE jellyfin LOGIN PASSWORD 'choose-a-password' CREATEDB;
CREATE DATABASE jellyfin OWNER jellyfin;
```

`CREATEDB` is optional, but recommended. Before an upgrade changes the database, Jellyfin copies it
(`CREATE DATABASE ... TEMPLATE`). If an upgrade fails, Jellyfin rolls back to that copy. Without `CREATEDB` the upgrade
still runs; Jellyfin logs a warning that it cannot roll back. With `CREATEDB`, Jellyfin can also create the
`jellyfin` database itself on first start.

Nothing else needs to be installed or configured on the PostgreSQL side. Jellyfin creates its tables, indexes and the two
small helper aggregates it needs (`min`/`max` for `uuid`) itself.

## 2. Point Jellyfin at it: `database.xml`

The database is chosen in `database.xml` in Jellyfin's configuration directory:

| Install | File |
|---|---|
| Debian/Ubuntu package | `/etc/jellyfin/database.xml` |
| Official Docker image | `/config/config/database.xml` |
| Windows | `%ProgramData%\Jellyfin\Server\config\database.xml` |
| Custom `--configdir` | `<configdir>/database.xml` |

Jellyfin writes a SQLite `database.xml` on first start. Stop Jellyfin and replace it with:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>Jellyfin-PostgreSQL</DatabaseType>
  <CustomProviderOptions>
    <Options>
      <CustomDatabaseOption><Key>Host</Key><Value>db.example.lan</Value></CustomDatabaseOption>
      <CustomDatabaseOption><Key>Port</Key><Value>5432</Value></CustomDatabaseOption>
      <CustomDatabaseOption><Key>Database</Key><Value>jellyfin</Value></CustomDatabaseOption>
      <CustomDatabaseOption><Key>Username</Key><Value>jellyfin</Value></CustomDatabaseOption>
      <CustomDatabaseOption><Key>Password</Key><Value>choose-a-password</Value></CustomDatabaseOption>
    </Options>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

Each option is either an [Npgsql connection string keyword][npgsql-keywords] or one of the provider settings below.
Common keywords are `Host`, `Port`, `Database`, `Username`, `Password`, `SSL Mode`, `Root Certificate`, `Timeout`,
`Command Timeout` and `Maximum Pool Size`. You can also put a complete connection string in a
`<ConnectionString>` element inside `<CustomProviderOptions>`. Options listed under `<Options>` override it.

| Provider setting | Default | Meaning |
|---|---|---|
| `migration-backup` | `true` | Copy the database before an upgrade changes it, so a failed upgrade can be rolled back. Set to `false` to skip the copy, e.g. for very large databases with their own backup routine. |
| `maintenance-database` | `postgres` | The database Jellyfin connects to when creating, copying or dropping databases. |
| `EnableSensitiveDataLogging` | `false` | Log the values of query parameters. For debugging only. |

To keep the password out of `database.xml`, leave out `Password` and set the standard PostgreSQL environment variable
`PGPASSWORD` for the Jellyfin process instead, or use a password file with the `Passfile` keyword. In the same way
`PGHOST`, `PGPORT`, `PGDATABASE` and `PGUSER` fill in whatever `database.xml` leaves out. When neither names a host,
database or user, Jellyfin uses `localhost`, `jellyfin` and `jellyfin`.

Keep `LockingBehavior` on `NoLock`. The PostgreSQL provider coordinates writes itself, see [How it behaves](#how-it-behaves).

Start Jellyfin. The log shows the connection it uses, with the password masked:

```
[INF] Jellyfin.Database.Providers.Postgres.PostgresDatabaseProvider: PostgreSQL connection string: Host=db.example.lan;Port=5432;Database=jellyfin;Username=jellyfin;Password=********;Application Name=Jellyfin
```

A new server starts with the setup wizard as usual. To bring an existing server's data along, follow the next section
instead.

## 3. Moving an existing server from SQLite to PostgreSQL

The move uses Jellyfin's own backup and restore. A backup made on SQLite can be restored into PostgreSQL, but only by
the same Jellyfin version that made it.

1. **Update and start once on SQLite.** Install this build and let the server start on its SQLite database, so that
   `jellyfin.db` is brought up to this version.
2. **Back up the database.** In the dashboard, open *Backups* and create a backup that includes the database. You can
   also do this through the API with `POST /Backup/Create` and the body `{"Database": true}`. The backup is a `.zip` in
   the `backups` folder of the data directory, e.g. `/var/lib/jellyfin/data/backups/` or `/config/data/backups/` in Docker.
3. **Stop Jellyfin**, and prepare PostgreSQL as in [step 1](#1-prepare-the-database-server).
4. **Replace `database.xml`** as in [step 2](#2-point-jellyfin-at-it-databasexml). Keep a copy of the old SQLite one.
5. **Start Jellyfin once with `--restore-archive`**, pointing at the backup:

   ```sh
   jellyfin --restore-archive /var/lib/jellyfin/data/backups/jellyfin-backup-20261013210426.zip
   ```

   With Docker, add it as the container command, e.g. `command: --restore-archive /config/data/backups/jellyfin-backup-....zip`.
   Jellyfin creates the database if needed, creates the tables, restores everything, and restarts itself on PostgreSQL.
   The log shows `The backup was made with another database provider, keeping the schema history of this database`.
6. **Remove `--restore-archive`** again, so the next start does not restore the backup a second time.

The restore leaves your `database.xml` alone, so the server stays on PostgreSQL. `jellyfin.db` stays untouched in the data
directory. To go back to SQLite, put the old `database.xml` back.

## Docker Compose example

```yaml
services:
  postgres:
    image: postgres:18
    environment:
      POSTGRES_USER: jellyfin
      POSTGRES_PASSWORD: choose-a-password
      POSTGRES_DB: jellyfin
    volumes:
      - ./postgres:/var/lib/postgresql
    restart: unless-stopped

  jellyfin:
    image: your-build-of-this-repository
    depends_on:
      - postgres
    environment:
      PGPASSWORD: choose-a-password
    volumes:
      - ./config:/config     # database.xml goes in ./config/config/
      - ./cache:/cache
      - /path/to/media:/media
    ports:
      - 8096:8096
    restart: unless-stopped
```

With this setup, `database.xml` only needs `Host` = `postgres`, plus `Database` and `Username`; the password comes from
`PGPASSWORD`. The `postgres:18` image creates the `jellyfin` user as a superuser, so backups before upgrades work out of
the box.

## How it behaves

The rest of Jellyfin was written and tested against SQLite. The PostgreSQL provider keeps that behaviour, so the
server works the same on either database:

- **Writes.** SQLite lets one transaction write at a time. Jellyfin relies on that when a library scan checks which
  items, genres and people exist and inserts the missing ones. On PostgreSQL every transaction takes a
  transaction-scoped advisory lock, so two scans cannot insert the same item at once. Reads, and single statements outside
  a transaction, still run concurrently.
- **Text.** Text columns use the `C` collation, as SQLite does: byte-order sorting, and case folding of ASCII letters
  only. Searches that ignore case on SQLite (`LIKE`) use `ILIKE` on PostgreSQL. Declared maximum lengths are not
  enforced, because SQLite never did. NUL characters, which PostgreSQL cannot store and which broken tags sometimes
  contain, are removed on save, so one bad file cannot fail a whole scan.
- **Sorting.** Items without a value sort first ascending and last descending, as on SQLite. "Last played, newest
  first" does not start with everything never played. Indexes on optional columns are built to match.
- **Times.** Times are stored as UTC `timestamp with time zone`. PostgreSQL keeps microseconds, where .NET keeps
  100 ns. Comparisons that relied on an exact round trip allow for that, so unchanged images are not regenerated and
  chapter images not extracted again.
- **Maintenance.** The *Optimize database* scheduled task runs `VACUUM (ANALYZE)`. Statistics are refreshed with
  `ANALYZE` after library scans. Autovacuum takes care of the rest.
- **Backups.** The dashboard backup works the same as on SQLite, and a backup restores into either database.

## Troubleshooting

| Log message | Meaning |
|---|---|
| `database "jellyfin" does not exist` | Create the database, or give the role `CREATEDB` so Jellyfin can create it. |
| `The database cannot be backed up before migrating, so a failed migration cannot be rolled back: ...` | The role lacks `CREATEDB`, or another client was connected to the database during the upgrade. The upgrade went ahead. |
| `The PostgreSQL option 'X' in database.xml is neither a provider option nor an Npgsql connection string keyword.` | Typo in a key in `database.xml`. |
| `The backup was made with another database provider by Jellyfin X. Moving data between database providers needs a backup made by the same version ...` | Restore the backup on SQLite first, start this version once, and make a new backup. |

## For developers

- The provider lives in `src/Jellyfin.Database/Jellyfin.Database.Providers.Postgres`. It has its own EF Core migrations,
  see [`src/Jellyfin.Database/readme.md`](../src/Jellyfin.Database/readme.md) for how to add one.
- Set `JELLYFIN_TEST_POSTGRES` to a connection string for a PostgreSQL server, with a role that may create databases,
  to run the database tests against PostgreSQL:

  ```sh
  docker run -d --name jf-pg -e POSTGRES_USER=jellyfin -e POSTGRES_PASSWORD=jellyfin -p 5432:5432 postgres:18
  export JELLYFIN_TEST_POSTGRES="Host=localhost;Username=jellyfin;Password=jellyfin"
  dotnet test tests/Jellyfin.Server.Implementations.Tests
  dotnet test tests/Jellyfin.Server.Integration.Tests
  ```

  The repository tests run in a fresh database each. The integration tests start the whole server on PostgreSQL.

[npgsql-keywords]: https://www.npgsql.org/doc/connection-string-parameters.html
