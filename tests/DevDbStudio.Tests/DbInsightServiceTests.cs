using DevDbStudio.Services.Dbms;

namespace DevDbStudio.Tests;

public class DbInsightServiceTests
{
    private sealed class FakeManager(QueryResult result) : IInstanceManager
    {
        public string? LastSql { get; private set; }

        public Task<QueryResult> ExecuteSqlAsync(Guid id, string sql, CancellationToken ct = default)
        {
            LastSql = sql;
            return Task.FromResult(result);
        }

        public IReadOnlyList<IDbmsProvider> Providers => throw new NotSupportedException();
        public IDbmsProvider? GetProvider(string key) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceListResult> RefreshStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceActionResult> StartAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceActionResult> StopAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceActionResult> DeleteAsync(Guid id, bool deleteVolume, CancellationToken ct = default) => throw new NotSupportedException();
        public string BuildConnectionString(DbInstance instance, bool maskPassword) => throw new NotSupportedException();
        public string? BuildNetworkConnectionString(DbInstance instance, bool maskPassword) => throw new NotSupportedException();
        public Task<InstanceActionResult> SetSharedNetworkAsync(Guid id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static QueryResult Ok(string stdout) => new(true, stdout, "", 0, TimeSpan.Zero);

    private const string Table =
        "connections\tmax_connections\tuser_databases\tsize_mb\n-----------\t---------------\t--------------\t-------\n3\t100\t2\t24.5\n\n(1 rows affected)";

    [Fact]
    public async Task ParsesRow_AndSubtractsTheProbeConnection()
    {
        var svc = new DbInsightService(new FakeManager(Ok(Table)));

        var r = await svc.GetAsync(new DbInstance { DbmsKey = "postgres" });

        Assert.True(r.Ok);
        Assert.Equal(2, r.Connections);
        Assert.Equal(100, r.MaxConnections);
        Assert.Equal(2, r.UserDatabases);
        Assert.Equal(24.5, r.SizeMb);
    }

    [Fact]
    public async Task ConnectionCount_NeverGoesNegative()
    {
        var table = Table.Replace("3\t100", "0\t100");
        var r = await new DbInsightService(new FakeManager(Ok(table))).GetAsync(new DbInstance { DbmsKey = "mysql" });

        Assert.Equal(0, r.Connections);
    }

    [Theory]
    [InlineData("postgres", "pg_stat_activity")]
    [InlineData("mariadb", "information_schema.processlist")]
    [InlineData("mysql", "information_schema.processlist")]
    [InlineData("mssql", "sys.dm_exec_sessions")]
    public async Task UsesTheQueryForTheDbms(string key, string expectedFragment)
    {
        var fake = new FakeManager(Ok(Table));

        await new DbInsightService(fake).GetAsync(new DbInstance { DbmsKey = key });

        Assert.Contains(expectedFragment, fake.LastSql);
    }

    [Fact]
    public async Task FailedQuery_ReturnsFailWithError()
    {
        var fake = new FakeManager(new QueryResult(false, "", "connection refused", 1, TimeSpan.Zero));

        var r = await new DbInsightService(fake).GetAsync(new DbInstance { DbmsKey = "postgres" });

        Assert.False(r.Ok);
        Assert.Contains("connection refused", r.Error);
    }

    [Fact]
    public async Task UnparseableOutput_ReturnsFail()
    {
        var r = await new DbInsightService(new FakeManager(Ok("nothing useful"))).GetAsync(new DbInstance { DbmsKey = "postgres" });

        Assert.False(r.Ok);
    }
}
