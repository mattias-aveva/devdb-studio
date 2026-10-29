using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDbStudio.Tests;

public class ReviewFixTests
{
    private sealed class FakeRunner(Func<string[], ProcessResult> reply) : IWslDockerRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
        {
            var args = dockerArgs.ToArray();
            Calls.Add(args);
            return Task.FromResult(reply(args));
        }
    }

    private sealed class MemoryRepo(params DbInstance[] items) : IInstanceRepository
    {
        public List<DbInstance> Items { get; } = [.. items];
        public bool RemoveTokenCancellable { get; private set; }

        public Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DbInstance>>(Items.ToList());
        public Task<DbInstance?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddAsync(DbInstance instance, CancellationToken ct = default) { Items.Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(DbInstance instance, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
        {
            RemoveTokenCancellable = ct.CanBeCanceled;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Items.RemoveAll(i => i.Id == id) > 0);
        }
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);

    // ---- ContainerEnvironment ---------------------------------------------------------------------------------

    [Fact]
    public void Extract_MovesRunEnvironmentOutOfArguments()
    {
        var (args, env) = ContainerEnvironment.Extract(
            ["run", "-d", "--name", "c", "-p", "127.0.0.1:1:2", "-e", "POSTGRES_PASSWORD=Pa;ss!", "-v", "v:/data", "img:1", "sh", "-c", "echo hi"]);

        Assert.Equal(["run", "-d", "--name", "c", "-p", "127.0.0.1:1:2", "-e", "POSTGRES_PASSWORD", "-v", "v:/data", "img:1", "sh", "-c", "echo hi"], args);
        Assert.Equal("Pa;ss!", env["POSTGRES_PASSWORD"]);
    }

    [Fact]
    public void Extract_LeavesClientOptionsAfterTheContainerAlone()
    {
        // mysql's own -e (execute) comes after the container name and must not be touched.
        var (args, env) = ContainerEnvironment.Extract(["exec", "-i", "-e", "MYSQL_PWD=secret", "box", "mysql", "-e", "SELECT=1"]);

        Assert.Equal(["exec", "-i", "-e", "MYSQL_PWD", "box", "mysql", "-e", "SELECT=1"], args);
        Assert.Equal(["MYSQL_PWD"], env.Keys);
    }

    [Fact]
    public void Extract_KeepsValuesThatCannotTravelInAnEnvironment()
    {
        var (args, env) = ContainerEnvironment.Extract(["exec", "-e", "A=line1\nline2", "box", "x"]);

        Assert.Empty(env);
        Assert.Contains("A=line1\nline2", args);
    }

    [Fact]
    public void Extract_IgnoresOtherSubcommands()
    {
        var (args, env) = ContainerEnvironment.Extract(["rm", "-f", "c"]);

        Assert.Equal(["rm", "-f", "c"], args);
        Assert.Empty(env);
    }

    [Fact]
    public void ForWsl_ListsEveryVariableInWslenv()
    {
        var wsl = ContainerEnvironment.ForWsl(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });

        Assert.NotNull(wsl);
        Assert.Contains("A", wsl!["WSLENV"].Split(':'));
        Assert.Contains("B", wsl["WSLENV"].Split(':'));
        Assert.Null(ContainerEnvironment.ForWsl(new Dictionary<string, string>()));
    }

    // ---- ProcessRunner ----------------------------------------------------------------------------------------

    private static (string File, string[] Args) Sleep() => OperatingSystem.IsWindows()
        ? ("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"])
        : ("sleep", ["30"]);

    [Fact]
    public async Task ProcessRunner_TimesOutWhileWritingStdinToAChildThatNeverReads()
    {
        var (file, args) = Sleep();
        var big = new string('x', 8 * 1024 * 1024);

        var r = await ProcessRunner.RunAsync(file, args, TimeSpan.FromSeconds(2), stdin: big);

        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task ProcessRunner_PassesEnvironmentVariables()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo %DBS_TEST_VAR%" })
            : ("sh", ["-c", "echo $DBS_TEST_VAR"]);

        var r = await ProcessRunner.RunAsync(file, args, TimeSpan.FromSeconds(20),
            environment: new Dictionary<string, string> { ["DBS_TEST_VAR"] = "s3cret;x" });

        Assert.True(r.ExitCode == 0, r.StdErr);
        Assert.Equal("s3cret;x", r.StdOut);
    }

    // ---- Postgres family data mount ---------------------------------------------------------------------------

    private static async Task<string> MountFor(Func<FakeRunner, IDbmsProvider> make, string tag)
    {
        var runner = new FakeRunner(a => a[0] == "run" ? Ok("id") : Ok("accepting connections"));
        var p = make(runner);
        await p.CreateAsync(new InstanceCreateRequest("n", p.Key, 5440, "Passw0rd!x", "appdb", tag), default);
        var run = runner.Calls.First(c => c[0] == "run");
        return run[Array.IndexOf(run, "-v") + 1].Split(':')[1];
    }

    private static Task<string> PgMount(string tag) =>
        MountFor(r => new PostgresProvider(r, NullLogger<PostgresProvider>.Instance), tag);

    private static Task<string> TsMount(string tag) =>
        MountFor(r => new TimescaleDbProvider(r, NullLogger<TimescaleDbProvider>.Instance), tag);

    [Theory]
    [InlineData("16", "/var/lib/postgresql/data")]
    [InlineData("17-alpine", "/var/lib/postgresql/data")]
    [InlineData("18", "/var/lib/postgresql")]
    [InlineData("latest", "/var/lib/postgresql")]
    [InlineData("alpine", "/var/lib/postgresql")]
    [InlineData("bookworm", "/var/lib/postgresql")]
    public async Task Postgres_DataMount_FollowsTheImageLayout(string tag, string expected) =>
        Assert.Equal(expected, await PgMount(tag));

    [Theory]
    [InlineData("latest-pg17", "/var/lib/postgresql/data")]
    [InlineData("2.17.2-pg16", "/var/lib/postgresql/data")]
    [InlineData("latest-pg18", "/var/lib/postgresql")]
    public async Task Timescale_DataMount_FollowsThePostgresMajor(string tag, string expected) =>
        Assert.Equal(expected, await TsMount(tag));

    // ---- Delete must finish its bookkeeping even when the caller is cancelled ---------------------------------

    [Fact]
    public async Task Delete_RemovesTheRecord_EvenWhenTheCallerIsCancelled()
    {
        var instance = new DbInstance
        {
            Id = Guid.NewGuid(),
            Name = "r",
            DbmsKey = RedisProvider.ProviderKey,
            ContainerName = "dbapp-redis-1",
            HostPort = 6390,
            DatabaseName = "0",
            Username = "default",
            Password = "Passw0rd!x",
            VolumeName = "v",
            ImageTag = "8",
        };
        var repo = new MemoryRepo(instance);
        var runner = new FakeRunner(_ => Ok());
        var manager = new InstanceManager(repo, [new RedisProvider(runner, NullLogger<RedisProvider>.Instance)], runner, NullLogger<InstanceManager>.Instance,
            new SharedContainerNetwork(runner, NullLogger<SharedContainerNetwork>.Instance, new TestContainerEngine()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await manager.DeleteAsync(instance.Id, deleteVolume: true, cts.Token);

        Assert.True(r.Success);
        Assert.Empty(repo.Items);
        Assert.False(repo.RemoveTokenCancellable);
        Assert.Contains(runner.Calls, c => c.SequenceEqual(["volume", "rm", "v"]));
    }
}
