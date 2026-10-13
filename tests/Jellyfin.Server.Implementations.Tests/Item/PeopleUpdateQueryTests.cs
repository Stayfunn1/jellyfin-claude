using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class PeopleUpdateQueryTests : SqliteDbTestFixture
{
    private readonly CommandRecorder _recorder;
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly PeopleRepository _people;

    public PeopleUpdateQueryTests()
        : this(new CommandRecorder())
    {
    }

    private PeopleUpdateQueryTests(CommandRecorder recorder)
        : base(recorder)
    {
        _recorder = recorder;
        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _itemId,
            Name = "Movie",
            Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie]
        });
        context.SaveChanges();
        _people = new PeopleRepository(CreateDbContextFactory(), new ItemTypeLookup(), Mock.Of<IItemQueryHelpers>());
    }

    [Theory]
    [InlineData("Hero")]
    [InlineData("HERO")]
    public void UnchangedCredits_DoNotWriteOrLookUpAllPeople(string role)
    {
        _people.UpdatePeople(_itemId, [new PersonInfo { Name = "Actor", Type = PersonKind.Actor, Role = "Hero" }]);
        _recorder.Commands.Clear();
        _people.UpdatePeople(_itemId, [new PersonInfo { Name = "actor", Type = PersonKind.Actor, Role = role }]);
        Assert.Single(_recorder.Commands);
        Assert.StartsWith("SELECT", _recorder.Commands[0].Sql, StringComparison.Ordinal);
        using var context = CreateDbContext();
        Assert.Equal("Hero", Assert.Single(context.PeopleBaseItemMap).Role);
    }

    [Fact]
    public void SortOrderChange_IsPersisted()
    {
        _people.UpdatePeople(_itemId, [new PersonInfo { Name = "Actor", Type = PersonKind.Actor, SortOrder = 1 }]);
        _people.UpdatePeople(_itemId, [new PersonInfo { Name = "Actor", Type = PersonKind.Actor, SortOrder = 2 }]);
        using var context = CreateDbContext();
        Assert.Equal(2, Assert.Single(context.PeopleBaseItemMap).SortOrder);
    }

    [Fact]
    public void UpdatePeople_GeneratedSqlUsesPeopleNameIndex()
    {
        ApplyMigration(new Jellyfin.Server.Implementations.Migrations.AddPeopleNameLowerIndex());
        _recorder.Commands.Clear();
        _people.UpdatePeople(_itemId, [
            new PersonInfo { Name = "Actor A", Type = PersonKind.Actor },
            new PersonInfo { Name = "Actor B", Type = PersonKind.Actor }
        ]);
        var query = Assert.Single(_recorder.Commands, c => c.Sql.Contains(IsPostgres ? "lower(p.\"Name\")" : "lower(\"p\".\"Name\")", StringComparison.Ordinal));
        Assert.Contains(Explain(query), line => line.Contains(IsPostgres ? "\"IX_Peoples_NameLower\"" : "SEARCH p USING INDEX IX_Peoples_NameLower", StringComparison.Ordinal));
    }

    private void ApplyMigration(Migration migration)
    {
        using var context = CreateDbContext();
        foreach (var operation in migration.UpOperations.Cast<SqlOperation>())
        {
            context.Database.ExecuteSqlRaw(operation.Sql);
        }
    }

    private string[] Explain(RecordedCommand query) => ExplainQueryPlan(query.Sql, query.Parameters);

    private sealed record RecordedCommand(string Sql, (string Name, object? Value)[] Parameters);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<RecordedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        private void Record(DbCommand command) => Commands.Add(new RecordedCommand(
            command.CommandText,
            command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToArray()));
    }
}
