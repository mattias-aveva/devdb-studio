using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDbStudio.Tests;

public class SharedContainerNetworkTests
{
    private sealed class Runner(Func<string[], ProcessResult> run) : IWslDockerRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var args = dockerArgs.ToArray();
            Calls.Add(args);
            return Task.FromResult(run(args));
        }
    }

    private static ProcessResult Ok(string output = "") => new(0, output, "", false);
    private static SharedContainerNetwork Network(IWslDockerRunner runner) => new(runner, NullLogger<SharedContainerNetwork>.Instance, new TestContainerEngine());

    private static IDbmsProvider Provider(string key, IWslDockerRunner runner) => key switch
    {
        "mssql" => new MssqlProvider(runner, NullLogger<MssqlProvider>.Instance),
        "postgres" => new PostgresProvider(runner, NullLogger<PostgresProvider>.Instance),
        "timescaledb" => new TimescaleDbProvider(runner, NullLogger<TimescaleDbProvider>.Instance),
        "mariadb" => new MariaDbProvider(runner, NullLogger<MariaDbProvider>.Instance),
        "mysql" => new MySqlProvider(runner, NullLogger<MySqlProvider>.Instance),
        "mongodb" => new MongoDbProvider(runner, NullLogger<MongoDbProvider>.Instance),
        "redis" => new RedisProvider(runner, NullLogger<RedisProvider>.Instance),
        "couchdb" => new CouchDbProvider(runner, NullLogger<CouchDbProvider>.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    [Theory]
    [InlineData(false, "[{\"Driver\":\"bridge\"}]")]
    [InlineData(true, "[{\"driver\":\"bridge\",\"dns_enabled\":true}]")]
    public async Task Ensure_CreatesOnlyMissingNetwork_AndSupportsBothEngines(bool exists, string inspection)
    {
        var runner = new Runner(a => a[1] switch
        {
            "ls" => Ok(exists ? "other\ndevdb-network\n" : "devdb-network-other\nunrelated-network\n"),
            "inspect" => Ok(inspection),
            _ => Ok()
        });
        var result = await Network(runner).EnsureAsync();
        Assert.True(result.Success, result.Message);
        Assert.Equal(exists ? 0 : 1, runner.Calls.Count(c => c.SequenceEqual(["network", "create", "--driver", "bridge", "devdb-network"])));
        Assert.Contains(runner.Calls, c => c.SequenceEqual(["network", "inspect", "devdb-network"]));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("unrelated-network"));
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("create")]
    [InlineData("inspect")]
    public async Task Ensure_EngineFailure_IsReported(string failingCommand)
    {
        var runner = new Runner(a => a[1] == failingCommand ? new(125, "", "test failure", false) : Ok());
        var result = await Network(runner).EnsureAsync();
        Assert.False(result.Success);
        Assert.Contains("test failure", result.StdErr);
    }

    [Theory]
    [InlineData("[{\"Driver\":\"host\"}]")]
    [InlineData("[{\"driver\":\"bridge\",\"dns_enabled\":false}]")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Ensure_IncompatibleOrInvalidNetwork_IsReported(string inspection)
    {
        var runner = new Runner(a => Ok(a[1] == "ls" ? "devdb-network" : inspection));
        Assert.False((await Network(runner).EnsureAsync()).Success);
        Assert.DoesNotContain(runner.Calls, c => c[1] == "create");
    }

    [Fact]
    public async Task Ensure_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Network(new Runner(_ => Ok())).EnsureAsync(cts.Token));
    }

    [Fact]
    public async Task Ensure_TimeoutEvenWithZeroExitCode_IsReported()
    {
        var runner = new Runner(_ => new(0, "", "", true));
        Assert.False((await Network(runner).EnsureAsync()).Success);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("postgres")]
    [InlineData("timescaledb")]
    [InlineData("mariadb")]
    [InlineData("mysql")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("couchdb")]
    public void Providers_SharedNetworkPreservesLoopback_AndPreviewAndConnectionFormats(string key)
    {
        var runner = new Runner(_ => Ok());
        var provider = Provider(key, runner);
        var request = new InstanceCreateRequest("test", key, 12345, "FakePass1!;127.0.0.1", "appdb", provider.DefaultImageTag);
        var original = provider.BuildRunArgs(request, Guid.Empty, "dbapp-test", "dbapp-test-data");
        Assert.Contains("dbapp-test-network", original);
        Assert.DoesNotContain("devdb-network", original);
        var shared = request with { SharedNetwork = true };
        var args = provider.BuildRunArgs(shared, Guid.Empty, "dbapp-test", "dbapp-test-data").ToArray();
        Assert.Equal("dbapp-test-network", args[Array.IndexOf(args, "--network") + 1]);
        Assert.Contains("devdb-network", args);
        Assert.Equal($"127.0.0.1:12345:{provider.DefaultPort}", args[Array.IndexOf(args, "-p") + 1]);

        var yaml = ComposePreview.Render(provider, shared, true);
        Assert.Contains("      - \"devdb-network\"", yaml);
        Assert.Contains("    name: \"devdb-network\"", yaml);
        Assert.Contains($"      - \"dbapp-{key}-xxxxxxxx-network\"", yaml);
        Assert.Contains("    external: true", yaml);
        Assert.Contains(provider.ImageName, yaml);
        Assert.Empty(ComposePreview.Parse(provider, shared, yaml, true).Ignored);

        var manager = new InstanceManager(new MemoryRepo(), [provider], runner, NullLogger<InstanceManager>.Instance,
            Network(runner));
        var instance = new DbInstance
        {
            DbmsKey = key,
            ContainerName = "dbapp-test",
            HostPort = 12345,
            SharedNetwork = true,
            DatabaseName = "appdb",
            Username = provider.AdminUser,
            Password = request.Password
        };
        var host = manager.BuildConnectionString(instance, false);
        var connection = manager.BuildNetworkConnectionString(instance, false);
        Assert.NotNull(connection);
        Assert.Contains("127.0.0.1", host);
        Assert.Contains($"dbapp-test{(key == "mssql" ? "," : key is "postgres" or "timescaledb" or "mariadb" or "mysql" ? ";Port=" : ":")}{provider.DefaultPort}", connection);
        Assert.DoesNotContain("12345", connection);
        Assert.Contains(key is "mongodb" or "redis" or "couchdb" ? Uri.EscapeDataString(request.Password) : ConnectionStrings.Value(request.Password), connection);
        Assert.DoesNotContain("FakePass", manager.BuildNetworkConnectionString(instance, true));
        Assert.Null(manager.BuildNetworkConnectionString(instance with { SharedNetwork = false }, false));
    }

    private sealed class MemoryRepo : IInstanceRepository
    {
        public List<DbInstance> Items { get; } = [];
        public Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DbInstance>>(Items);
        public Task<DbInstance?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddAsync(DbInstance instance, CancellationToken ct = default) { Items.Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(DbInstance instance, CancellationToken ct = default)
        {
            Items[Items.FindIndex(i => i.Id == instance.Id)] = instance;
            return Task.CompletedTask;
        }
        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_UsesPerInstanceChoice_PersistsMembership_AndStopsOnNetworkFailure(bool enabled, bool failNetwork)
    {
        var runner = new Runner(a => a[0] == "network"
            ? failNetwork ? new(125, "", "network failed", false) : Ok(a[1] == "inspect" ? "[{\"Driver\":\"bridge\"}]" : "devdb-network")
            : Ok("PONG"));
        var repo = new MemoryRepo();
        var provider = Provider("redis", runner);
        var manager = new InstanceManager(repo, [provider], runner, NullLogger<InstanceManager>.Instance,
            Network(runner));
        var result = await manager.CreateAsync(new("test", "redis", 16379, "FakePass1!", "db0", "8", enabled));
        Assert.Equal(!failNetwork, result.Success);
        if (failNetwork)
        {
            Assert.Empty(repo.Items);
            Assert.DoesNotContain(runner.Calls, c => c[0] == "run");
        }
        else
        {
            Assert.Equal(enabled, Assert.Single(repo.Items).SharedNetwork);
            Assert.True(Assert.Single(repo.Items).NetworkChangesSupported);
            Assert.Equal(enabled, runner.Calls.Any(c => c.SequenceEqual(["network", "inspect", "devdb-network"])));
            Assert.Equal(enabled, Assert.Single(runner.Calls, c => c[0] == "run").Contains("devdb-network"));
            Assert.Contains(runner.Calls, c => c[0] == "network" && c[1] == "create" && c[^1].EndsWith("-network"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Start_UsesRecordedMembership(bool shared, bool failNetwork)
    {
        var runner = new Runner(a => a[0] == "network"
            ? failNetwork ? new(125, "", "network failed", false) : Ok(a[1] == "inspect" ? "[{\"Driver\":\"bridge\"}]" : "devdb-network")
            : Ok());
        var repo = new MemoryRepo();
        var instance = new DbInstance { Id = Guid.NewGuid(), DbmsKey = "redis", ContainerName = "dbapp-redis-test", SharedNetwork = shared };
        repo.Items.Add(instance);
        var manager = new InstanceManager(repo, [Provider("redis", runner)], runner, NullLogger<InstanceManager>.Instance,
            Network(runner));
        var result = await manager.StartAsync(instance.Id);
        Assert.Equal(!failNetwork, result.Success);
        Assert.Equal(shared, runner.Calls.Any(c => c[0] == "network"));
        Assert.Equal(!failNetwork, runner.Calls.Any(c => c[0] == "start"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Membership_ConnectsOrDisconnectsOnlyWhenNeeded(bool enabled, bool attached)
    {
        var runner = new Runner(a => a[0] == "inspect" ? Ok(attached ? """{"devdb-network":{},"private":{}}""" : """{"private":{}}""")
            : a[1] == "inspect" ? Ok("""[{"Driver":"bridge"}]""") : Ok("devdb-network"));
        var result = await Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, enabled);
        Assert.True(result.Success);
        Assert.Equal(enabled ? "Application container access enabled." : "Application container access disabled. Loopback access remains available.", result.Message);
        Assert.Equal(attached != enabled, runner.Calls.Any(c => c.SequenceEqual(
            ["network", enabled ? "connect" : "disconnect", "devdb-network", "dbapp-test"])));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("private") || c[0] is "stop" or "rm");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Toggle_PersistsOnlyAfterEngineSuccess(bool enabled, bool fail)
    {
        var runner = new Runner(a => a[0] == "inspect" ? Ok(enabled ? "{}" : """{"devdb-network":{}}""")
            : a[1] is "connect" or "disconnect" && fail ? new(125, "", "test failure", false)
            : a[1] == "inspect" ? Ok("""[{"Driver":"bridge"}]""") : Ok("devdb-network"));
        var repo = new MemoryRepo();
        var instance = new DbInstance
        {
            Id = Guid.NewGuid(),
            ContainerName = "dbapp-test",
            DbmsKey = "redis",
            SharedNetwork = !enabled,
            NetworkChangesSupported = true
        };
        repo.Items.Add(instance);
        var manager = new InstanceManager(repo, [Provider("redis", runner)], runner, NullLogger<InstanceManager>.Instance, Network(runner));
        var result = await manager.SetSharedNetworkAsync(instance.Id, enabled);
        Assert.Equal(!fail, result.Success);
        Assert.Equal(fail ? !enabled : enabled, Assert.Single(repo.Items).SharedNetwork);
    }

    [Fact]
    public async Task Toggle_OlderInstance_IsNotChanged()
    {
        var runner = new Runner(_ => throw new InvalidOperationException("No engine calls expected"));
        var repo = new MemoryRepo();
        var instance = new DbInstance { Id = Guid.NewGuid(), DbmsKey = "redis" };
        repo.Items.Add(instance);
        var manager = new InstanceManager(repo, [Provider("redis", runner)], runner, NullLogger<InstanceManager>.Instance, Network(runner));
        Assert.False((await manager.SetSharedNetworkAsync(instance.Id, true)).Success);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    public async Task Membership_InvalidInspection_IsReported(string output)
    {
        var runner = new Runner(_ => Ok(output));
        Assert.False((await Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, false)).Success);
    }

    [Fact]
    public async Task Delete_RemovesPrivateNetwork_ButNeverSharedNetwork()
    {
        var runner = new Runner(_ => Ok());
        var result = await ContainerLifecycle.DeleteContainerAsync(runner,
            new DbInstance { ContainerName = "dbapp-test", NetworkChangesSupported = true }, false, default);
        Assert.True(result.Success);
        Assert.Contains(runner.Calls, c => c.SequenceEqual(["network", "rm", "dbapp-test-network"]));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("devdb-network") || c[0] == "volume");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_PrivateNetworkFailure_DoesNotRunOrRemoveExistingResources(bool timeout)
    {
        var runner = new Runner(_ => new(timeout ? 0 : 125, "", "private network failure", timeout));
        var result = await Provider("redis", runner).CreateAsync(new("test", "redis", 16379, "FakePass1!", "db0", "8"), default);
        Assert.False(result.Success);
        var command = Assert.Single(runner.Calls);
        Assert.Equal("network", command[0]);
        Assert.Equal("create", command[1]);
    }

    [Fact]
    public async Task Create_RunFailure_CleansUpPrivateNetwork()
    {
        var runner = new Runner(a => a[0] == "run" ? new(125, "", "run failed", false) : Ok());
        var result = await Provider("redis", runner).CreateAsync(new("test", "redis", 16379, "FakePass1!", "db0", "8"), default);
        Assert.False(result.Success);
        Assert.Contains(runner.Calls, c => c[0] == "network" && c[1] == "rm" && c[^1].EndsWith("-network"));
        Assert.DoesNotContain(runner.Calls, c => c.SequenceEqual(["network", "rm", "devdb-network"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_InspectionFailureOrTimeout_IsReported(bool timeout)
    {
        var runner = new Runner(_ => new(timeout ? 0 : 125, "", "inspection failure", timeout));
        var result = await Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, false);
        Assert.False(result.Success);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Membership_Cancellation_PropagatesWithoutMutation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new Runner(_ => Ok());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, false, cts.Token));
        Assert.Empty(runner.Calls);
    }
}
