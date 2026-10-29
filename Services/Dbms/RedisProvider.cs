using System.Diagnostics;

namespace DevDbStudio.Services.Dbms;

/// <summary>Redis (key-value store). Commands are sent to redis-cli inside the container, one per line.</summary>
public sealed class RedisProvider(IWslDockerRunner docker, ILogger<RedisProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "redis";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(60);

    public string Key => ProviderKey;
    public string DisplayName => "Redis";
    public int DefaultPort => 6379;
    public string DefaultImageTag => "8";
    // Redis has numbered databases instead of named ones; the create form does not ask for one.
    public string DefaultDatabase => "db0";
    public string ImageName => "docker.io/library/redis";
    public string AdminUser => "default";
    public string NamePrefix => "redis";

    public bool SupportsTables => false;
    public bool UsesDatabaseName => false;
    public string QueryLanguage => "Redis commands (one per line)";
    public string SampleQuery => "SET greeting \"Hello\"\nGET greeting\nKEYS *";

    public async Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct)
    {
        var errors = InstanceValidation.Validate(req);
        if (errors.Count > 0) return InstanceCreateResult.Fail(string.Join(" ", errors));

        var id = Guid.NewGuid();
        var shortId = id.ToString("N")[..8];
        var container = $"dbapp-{Key}-{shortId}";
        var volume = $"{container}-data";
        var tag = req.ImageTag.Trim();

        try
        {
            var network = await ContainerLifecycle.CreatePrivateNetworkAsync(docker, container, ct);
            if (!network.Success) return InstanceCreateResult.Fail(network.Message, network.StdErr);
            var run = await docker.RunAsync(BuildRunArgs(req, id, container, volume), PullAndRunTimeout, ct: ct);

            if (run.ExitCode != 0)
                return await ContainerLifecycle.FailedRunAsync(docker, log, run, container, volume);

            var instance = new DbInstance
            {
                Id = id,
                Name = req.Name.Trim(),
                DbmsKey = Key,
                ContainerName = container,
                HostPort = req.HostPort,
                CreatedUtc = DateTime.UtcNow,
                Status = InstanceStatus.Running,
                DatabaseName = "0",
                Username = AdminUser,
                Password = req.Password,
                VolumeName = volume,
                ImageTag = tag,
                SharedNetwork = req.SharedNetwork,
                NetworkChangesSupported = true,
            };

            var ready = await WaitUntilReadyAsync(instance, ct);
            if (ready.Error is not null)
                return new InstanceCreateResult(true, instance, $"Container started, but {DisplayName} is not confirmed ready: {ready.Error}", ready.Detail);
            return new InstanceCreateResult(true, instance, $"Instance created and {DisplayName} is ready.");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(container, volume);
            throw;
        }
    }

    public string PasswordEnvVar => "REDIS_PASSWORD";
    public string? DatabaseEnvVar => null;

    public IReadOnlyList<string> BuildRunArgs(InstanceCreateRequest req, Guid id, string container, string volume) =>
    [
        "run", "-d", "--name", container,
        .. SharedContainerNetwork.RunArgs(req, container),
        "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
        // Loopback only: the password is the sole protection of this server.
        "-p", $"127.0.0.1:{req.HostPort}:6379",
        // The password stays out of the command line; the shell expands it from the environment.
        "-e", $"REDIS_PASSWORD={req.Password}",
        "-v", $"{volume}:/data",
        $"{ImageName}:{req.ImageTag.Trim()}",
        "sh", "-c", "exec redis-server --requirepass \"$REDIS_PASSWORD\" --appendonly yes"
    ];

    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StartContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StopContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct) =>
        ContainerLifecycle.DeleteContainerAsync(docker, instance, deleteVolume, ct);

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string script, CancellationToken ct) =>
        RunCliAsync(instance, script, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword, string host = "127.0.0.1") =>
        $"redis://{Uri.EscapeDataString(i.Username)}:{(maskPassword ? "*****" : Uri.EscapeDataString(i.Password))}@{host}:{i.HostPort}/{i.DatabaseName}";

    private async Task<QueryResult> RunCliAsync(DbInstance instance, string script, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // The password travels as an environment variable and the commands on stdin.
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"REDISCLI_AUTH={instance.Password}",
            instance.ContainerName,
            "redis-cli", "-h", "127.0.0.1", "-p", "6379", "-n", DatabaseIndex(instance.DatabaseName), "--no-auth-warning"
        ], timeout, script.TrimEnd() + "\n", ct);

        // redis-cli exits 0 even when a command is rejected, so error replies must be detected in the output.
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut && !HasErrorReply(r.StdOut), r.StdOut, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    internal static bool HasErrorReply(string stdout) =>
        stdout.Split('\n').Any(l => l.TrimStart().StartsWith("(error)", StringComparison.Ordinal));

    private static string DatabaseIndex(string name) => int.TryParse(name, out var n) && n is >= 0 and <= 255 ? n.ToString() : "0";

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RunCliAsync(instance, "PING", TimeSpan.FromSeconds(15), ct);
            if (last.IsSuccess && last.StdOut.Contains("PONG", StringComparison.Ordinal)) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return ("timed out waiting for the server to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
    }

    private Task CleanupAsync(string container, string volume) =>
        ContainerLifecycle.CleanupAsync(docker, log, container, volume);
}
