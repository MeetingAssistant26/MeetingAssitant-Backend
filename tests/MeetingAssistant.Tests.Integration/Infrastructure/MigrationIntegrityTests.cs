using System.Data;
using FluentAssertions;
using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public sealed class MigrationIntegrityTests
{
    private const string AdminConnectionEnv = "POSTGRES_SCHEMA_TEST_ADMIN_CONNECTION";

    [Fact]
    public void All_migration_source_files_should_be_discoverable_by_ef()
    {
        var repoRoot = FindRepositoryRoot();
        var migrationsDirectory = Path.Combine(repoRoot.FullName, "MeetingAssistant", "Migrations");

        var migrationSourceIds = Directory
            .EnumerateFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name) && char.IsDigit(name[0]))
            .Select(name => name!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        using var db = CreateDbContext("Host=localhost;Database=meetingassistant_migration_discovery;Username=test;Password=test");
        var discoveredMigrationIds = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);

        migrationSourceIds
            .Where(id => !discoveredMigrationIds.Contains(id))
            .Should()
            .BeEmpty("every migration source file must have EF migration metadata, otherwise production startup Migrate() will silently skip it");
    }

    [Fact]
    public async Task Fresh_postgres_migration_should_match_current_ef_model_columns()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(AdminConnectionEnv);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            return;
        }

        var databaseName = $"meetingassistant_schema_test_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var migratedConnectionString = WithDatabase(adminConnectionString, databaseName);
            await using var db = CreateDbContext(migratedConnectionString);

            await db.Database.MigrateAsync();

            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
            pendingMigrations.Should().BeEmpty("a fresh migrated database must have no pending EF migrations");

            var discoveredMigrations = db.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
            var appliedMigrations = await ReadAppliedMigrationIdsAsync(db);
            appliedMigrations.Should().Equal(discoveredMigrations);

            var missingColumns = await FindModelColumnsMissingFromDatabaseAsync(db);
            missingColumns.Should().BeEmpty("the migrated Postgres schema must contain every mapped EF model column");
        }
        finally
        {
            await DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(
            options,
            new HttpContextAccessor(),
            new StaticTenantProvider(),
            new NoopPublisher());
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync(ApplicationDbContext db)
    {
        var ids = new List<string>();
        var connection = db.Database.GetDbConnection();
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<string>> FindModelColumnsMissingFromDatabaseAsync(ApplicationDbContext db)
    {
        var expectedColumns = db.Model.GetEntityTypes()
            .SelectMany(GetMappedColumns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema, table_name, column_name
            FROM information_schema.columns
            WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actualColumns.Add(ColumnKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return expectedColumns
            .Where(column => !actualColumns.Contains(column))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetMappedColumns(IEntityType entityType)
    {
        var tableName = entityType.GetTableName();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            yield break;
        }

        var schema = entityType.GetSchema() ?? "public";
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        foreach (var property in entityType.GetProperties())
        {
            var columnName = property.GetColumnName(storeObject);
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                yield return ColumnKey(schema, tableName, columnName);
            }
        }
    }

    private static string ColumnKey(string schema, string table, string column) => $"{schema}.{table}.{column}";

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText = """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)}";
        await drop.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName
        };

        return builder.ConnectionString;
    }

    private static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingAssistant.sln")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new DirectoryNotFoundException("Could not locate MeetingAssistant.sln from test output directory.");
    }

    private sealed class StaticTenantProvider : ITenantProvider
    {
        public Guid? CurrentOrganizationId => null;
    }

    private sealed class NoopPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
