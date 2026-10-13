# How to run EFCore migrations

This shall provide context on how to work with entity frameworks multi provider migration feature.

Jellyfin supports multiple database providers: SQLite as its default, and PostgreSQL (see [docs/postgresql.md](../../docs/postgresql.md)).

Each provider has its own set of migrations, as they contain provider specific instructions to migrate the specific changes to their respective systems.

When creating a new migration, you always have to create migrations for all providers. Run the Entity Framework tool once per provider project; each project has a design time factory that builds the model with that provider's conventions, so no database is needed.

The examples are made from the root folder of the project e.g for codespaces `/workspaces/jellyfin`

```cmd
dotnet ef migrations add {MIGRATION_NAME} --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite" --output-dir Migrations
dotnet ef migrations add {MIGRATION_NAME} --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Postgres" --output-dir Migrations
```

SQL that a migration runs by hand (`migrationBuilder.Sql`) is provider specific and has to be written for each provider. The PostgreSQL `InitialCreate` migration also creates two things that are not part of the model: the `min`/`max` aggregates for `uuid` used by the item grouping queries, and the `IX_Peoples_NameLower` expression index.

To check a PostgreSQL migration against a real server, run the tests with `JELLYFIN_TEST_POSTGRES` set, see [docs/postgresql.md](../../docs/postgresql.md#for-developers).

If you get the error: `Run "dotnet tool restore" to make the "dotnet-ef" command available.` Run `dotnet restore`.

in the event that you get the error: `System.UnauthorizedAccessException: Access to the path '/src/Jellyfin.Database' is denied.` you have to restore as sudo and then run `ef migrations` as sudo too.
