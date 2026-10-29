using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDbStudio.Tests;

public class FollowUpFixTests
{
    private sealed class AsyncRunner(Func<string[], Task<ProcessResult>> reply) : IWslDockerRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
        {
            var args = dockerArgs.ToArray();
            lock (Calls) Calls.Add(args);
            return reply(args);
        }
    }

    private sealed class TrackingRepo(params DbInstance[] items) : IInstanceRepository
    {
        public List<DbInstance> Items { get; } = [.. items];
        public List<DbInstance> Updates { get; } = [];

        public Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DbInstance>>(Items.ToList());
        public Task<DbInstance?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddAsync(DbInstance instance, CancellationToken ct = default) { Items.Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(DbInstance instance, CancellationToken ct = default)
        {
            Updates.Add(instance);
            var idx = Items.FindIndex(i => i.Id == instance.Id);
            if (idx >= 0) Items[idx] = instance;
            return Task.CompletedTask;
        }
        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.RemoveAll(i => i.Id == id) > 0);
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);

    private static DbInstance Instance(InstanceStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Name = "r",
        DbmsKey = RedisProvider.ProviderKey,
        ContainerName = "dbapp-redis-abcd1234",
        HostPort = 6390,
        Status = status,
        DatabaseName = "0",
        Username = "default",
        Password = "Passw0rd!x",
        VolumeName = "dbapp-redis-abcd1234-data",
        ImageTag = "8",
    };

    // ---- Connection strings ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a=b", "\"a=b\"")]
    [InlineData(" pad", "\" pad\"")]
    public void ConnectionStringValue_QuotesOnlyWhenNeeded(string input, string expected) =>
        Assert.Equal(expected, ConnectionStrings.Value(input));

    [Fact]
    public void SqlServerConnectionString_QuotesSpecialPassword_AndMasks()
    {
        var p = new MssqlProvider(new AsyncRunner(_ => Task.FromResult(Ok())), NullLogger<MssqlProvider>.Instance);
        var i = Instance(InstanceStatus.Running) with { DbmsKey = "mssql", Username = "sa", Password = "a;b\"c" };

        Assert.Contains("\"a;b\"\"c\"", p.BuildConnectionString(i, maskPassword: false));
        Assert.DoesNotContain("a;b", p.BuildConnectionString(i, maskPassword: true));
    }

    // ---- Redis error replies -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("(error) ERR unknown command 'FOO'", true)]
    [InlineData("OK\n(error) WRONGTYPE Operation against a key", true)]
    [InlineData("PONG", false)]
    [InlineData("\"(error) inside a value\"", false)]
    public void Redis_DetectsErrorReplies(string stdout, bool expected) =>
        Assert.Equal(expected, RedisProvider.HasErrorReply(stdout));

    [Fact]
    public async Task Redis_ErrorReply_IsReportedAsFailure()
    {
        var runner = new AsyncRunner(a => Task.FromResult(Ok("(error) ERR unknown command 'FOO'")));
        var p = new RedisProvider(runner, NullLogger<RedisProvider>.Instance);

        var r = await p.ExecuteSqlAsync(Instance(InstanceStatus.Running), "FOO", default);

        Assert.False(r.IsSuccess);
    }

    // ---- Short-id collision ------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_NameConflict_DoesNotRemoveTheExistingContainerOrVolume()
    {
        var conflict = new ProcessResult(125, "", "docker: Error response from daemon: Conflict. The container name \"/dbapp-redis-x\" is already in use by container \"abc\".", false);
        var runner = new AsyncRunner(a => Task.FromResult(a[0] == "run" ? conflict : Ok()));
        var p = new RedisProvider(runner, NullLogger<RedisProvider>.Instance);

        var r = await p.CreateAsync(new InstanceCreateRequest("n", RedisProvider.ProviderKey, 6391, "Passw0rd!x", "db0", "8"), default);

        Assert.False(r.Success);
        Assert.Contains(runner.Calls, c => c[0] == "run");
        Assert.DoesNotContain(runner.Calls, c => c[0] is "rm" or "volume");
    }

    [Fact]
    public async Task Create_OtherRunFailure_StillCleansUp()
    {
        var runner = new AsyncRunner(a => Task.FromResult(a[0] == "run" ? new ProcessResult(125, "", "port is already allocated", false) : Ok()));
        var p = new RedisProvider(runner, NullLogger<RedisProvider>.Instance);

        await p.CreateAsync(new InstanceCreateRequest("n", RedisProvider.ProviderKey, 6391, "Passw0rd!x", "db0", "8"), default);

        Assert.Contains(runner.Calls, c => c[0] == "rm");
        Assert.Contains(runner.Calls, c => c[0] == "volume");
    }

    // ---- MongoDB readiness -------------------------------------------------------------------------------------

    [Fact]
    public async Task Mongo_Create_WaitsForInitMarker_BeforeReportingReady()
    {
        var logCalls = 0;
        var runner = new AsyncRunner(a =>
        {
            if (a[0] == "run") return Task.FromResult(Ok("id"));
            if (a[0] == "logs")
                return Task.FromResult(Ok(++logCalls < 2 ? "starting" : "MongoDB init process complete; ready for start up."));
            return Task.FromResult(Ok("1"));
        });
        var p = new MongoDbProvider(runner, NullLogger<MongoDbProvider>.Instance);

        var r = await p.CreateAsync(new InstanceCreateRequest("m", MongoDbProvider.ProviderKey, 27019, "Passw0rd!x", "appdb", "8"), default);

        Assert.Contains("is ready", r.Message);
        Assert.True(logCalls >= 2);
        // No ping before the marker appeared.
        var firstPing = runner.Calls.FindIndex(c => c[0] == "exec");
        var secondLogs = runner.Calls.FindIndex(c => c[0] == "logs") is var l1 && l1 >= 0
            ? runner.Calls.FindIndex(l1 + 1, c => c[0] == "logs") : -1;
        Assert.True(firstPing > secondLogs);
    }

    // ---- Stale status ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshStatus_PersistsChange_UsingTheCurrentRecord()
    {
        var inst = Instance(InstanceStatus.Running);
        var repo = new TrackingRepo(inst);
        var runner = new AsyncRunner(a => Task.FromResult(Ok($"{inst.ContainerName}|exited")));
        var mgr = new InstanceManager(repo, [new RedisProvider(runner, NullLogger<RedisProvider>.Instance)], runner, NullLogger<InstanceManager>.Instance,
            new SharedContainerNetwork(runner, NullLogger<SharedContainerNetwork>.Instance, new TestContainerEngine()));

        var res = await mgr.RefreshStatusAsync();

        Assert.Equal(InstanceStatus.Stopped, res.Instances.Single().Status);
        Assert.Equal(InstanceStatus.Stopped, repo.Items.Single().Status);
    }

    [Fact]
    public async Task RefreshStatus_DoesNotOverwriteInstanceWithOperationInFlight()
    {
        var inst = Instance(InstanceStatus.Stopped);
        var repo = new TrackingRepo(inst);
        var startGate = new TaskCompletionSource<ProcessResult>();
        var runner = new AsyncRunner(a => a[0] switch
        {
            "start" => startGate.Task,
            "ps" => Task.FromResult(Ok($"{inst.ContainerName}|running")),
            _ => Task.FromResult(Ok()),
        });
        var mgr = new InstanceManager(repo, [new RedisProvider(runner, NullLogger<RedisProvider>.Instance)], runner, NullLogger<InstanceManager>.Instance,
            new SharedContainerNetwork(runner, NullLogger<SharedContainerNetwork>.Instance, new TestContainerEngine()));

        var start = mgr.StartAsync(inst.Id);
        await Task.Delay(100); // let Start take the per-instance gate and block in "docker start"
        var res = await mgr.RefreshStatusAsync();

        Assert.Equal(InstanceStatus.Running, res.Instances.Single().Status); // the live view is still right
        Assert.Empty(repo.Updates);                                          // but nothing is persisted mid-operation

        startGate.SetResult(Ok());
        await start;
        Assert.Equal(InstanceStatus.Running, repo.Items.Single().Status);
    }
}
