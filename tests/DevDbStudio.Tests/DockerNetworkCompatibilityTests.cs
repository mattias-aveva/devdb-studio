using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDbStudio.Tests;

public class DockerNetworkCompatibilityTests
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

    private static ProcessResult Ok(string output) => new(0, output, "", false);
    private static SharedContainerNetwork Network(Runner runner, string executable = "docker") =>
        new(runner, NullLogger<SharedContainerNetwork>.Instance, new TestContainerEngine(executable));

    [Theory]
    [InlineData("25.0.0|25.0.0|1.44|1.44", true)]
    [InlineData("28.3.3|28.3.3|1.51|1.51", true)]
    [InlineData("25.0.0-rc.1|25.0.0+build|1.44|1.44", true)]
    [InlineData("24.0.9|25.0.0|1.44|1.44", false)]
    [InlineData("25.0.0|24.0.9|1.44|1.43", false)]
    [InlineData("25.0.0|25.0.0|1.43|1.44", false)]
    [InlineData("25.0.0|25.0.0|1.44|1.43", false)]
    public async Task Check_RequiresSupportedClientEngineAndApi(string output, bool supported)
    {
        var runner = new Runner(_ => Ok(output));
        var result = await DockerNetworkCompatibility.CheckAsync(runner, default);
        Assert.Equal(supported, result.Success);
        Assert.Equal(["version", "--format", DockerNetworkCompatibility.VersionFormat], Assert.Single(runner.Calls));
        if (!supported)
        {
            Assert.Contains("25.0", result.Message);
            Assert.Contains("1.44", result.Message);
            Assert.Contains("Upgrade", result.Message);
            Assert.Contains("Loopback-only", result.Message);
            Assert.Contains("Detected CLI", result.StdErr);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("25.0.0")]
    [InlineData("25.0.0|25.0.0||1.44")]
    [InlineData("25.0.0|invalid|1.44|1.44")]
    [InlineData("25.0.0|25.0.0|1.44|1.44|extra")]
    public async Task Check_InvalidVersionOutput_IsReported(string output)
    {
        var result = await DockerNetworkCompatibility.CheckAsync(new Runner(_ => Ok(output)), default);
        Assert.False(result.Success);
        Assert.Contains("Could not read", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_FailureOrTimeout_IsReported(bool timeout)
    {
        var result = await DockerNetworkCompatibility.CheckAsync(
            new Runner(_ => new(timeout ? 0 : 1, "", "test error", timeout)), default);
        Assert.False(result.Success);
        Assert.Contains("test error", result.StdErr);
        Assert.Equal(timeout, result.Message.Contains("timed out"));
    }

    [Fact]
    public async Task Check_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DockerNetworkCompatibility.CheckAsync(new Runner(_ => Ok("")), cts.Token));
    }

    [Theory]
    [InlineData("24.0.9|24.0.9|1.43|1.43", false)]
    [InlineData("25.0.0|25.0.0|1.44|1.44", true)]
    public async Task Ensure_ChecksCompatibilityBeforeNetworkChanges(string version, bool supported)
    {
        var runner = new Runner(args => args[0] == "version" ? Ok(version)
            : args[1] == "ls" ? Ok("devdb-network") : Ok("""[{"Driver":"bridge"}]"""));
        var result = await Network(runner).EnsureAsync();
        Assert.Equal(supported, result.Success);
        Assert.Equal("version", runner.Calls[0][0]);
        Assert.Equal(supported ? 3 : 1, runner.Calls.Count);
    }

    [Fact]
    public async Task Ensure_Podman_DoesNotCheckDockerVersions()
    {
        var runner = new Runner(args => args[1] == "ls" ? Ok("devdb-network")
            : Ok("""[{"driver":"bridge","dns_enabled":true}]"""));
        Assert.True((await Network(runner, "podman").EnsureAsync()).Success);
        Assert.DoesNotContain(runner.Calls, args => args[0] == "version");
    }

    [Fact]
    public async Task Enable_UnsupportedDocker_DoesNotInspectOrConnectContainer()
    {
        var runner = new Runner(_ => Ok("24.0.9|24.0.9|1.43|1.43"));
        var result = await Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, true);
        Assert.False(result.Success);
        Assert.Equal("version", Assert.Single(runner.Calls)[0]);
    }

    [Fact]
    public async Task Disable_UnsupportedDocker_RemainsAvailable()
    {
        var runner = new Runner(args => args[0] == "inspect" ? Ok("""{"devdb-network":{}}""") : Ok(""));
        Assert.True((await Network(runner).SetMembershipAsync(new DbInstance { ContainerName = "dbapp-test" }, false)).Success);
        Assert.DoesNotContain(runner.Calls, args => args[0] == "version");
        Assert.Contains(runner.Calls, args => args.SequenceEqual(["network", "disconnect", "devdb-network", "dbapp-test"]));
    }

    [Theory]
    [InlineData("24.0.9|24.0.9|1.43|1.43", CheckStatus.Fail)]
    [InlineData("25.0.0|25.0.0|1.44|1.44", CheckStatus.Pass)]
    public async Task Setup_ShowsCompatibilityAndUpgradeInstructions(string output, CheckStatus expected)
    {
        var service = new PrerequisiteCheckService(Options.Create(new WslOptions()), new TestContainerEngine("docker"),
            new Runner(_ => Ok(output)));
        var daemon = new CheckResult("engine-daemon", "Docker daemon reachable", CheckStatus.Pass, "", "", "", "", "");
        var result = await service.CheckDockerNetworkAsync(daemon, default);
        Assert.Equal(expected, result.Status);
        Assert.Equal("docker-network-version", result.CheckId);
        Assert.Contains("25.0", result.WhyItMatters);
        Assert.Equal(expected == CheckStatus.Fail, result.HelpMarkdown.Contains("Upgrade"));
    }

    [Fact]
    public async Task Setup_UnreachableDaemon_SkipsCompatibilityCheck()
    {
        var runner = new Runner(_ => throw new InvalidOperationException("No engine calls expected"));
        var service = new PrerequisiteCheckService(Options.Create(new WslOptions()), new TestContainerEngine("docker"), runner);
        var daemon = new CheckResult("engine-daemon", "Docker daemon reachable", CheckStatus.Fail, "", "", "", "", "");
        Assert.Equal(CheckStatus.Unknown, (await service.CheckDockerNetworkAsync(daemon, default)).Status);
        Assert.Empty(runner.Calls);
    }
}
