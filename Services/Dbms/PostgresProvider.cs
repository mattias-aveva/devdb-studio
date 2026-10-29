using System.Diagnostics;
using System.Globalization;

namespace DevDbStudio.Services.Dbms;

public sealed class PostgresProvider(IWslDockerRunner docker, ILogger<PostgresProvider> log) : PostgresFamilyProvider(docker, log)
{
    public const string ProviderKey = "postgres";

    public override string Key => ProviderKey;
    public override string DisplayName => "PostgreSQL";
    public override string DefaultImageTag => "17";
    // Fully qualified so Podman does not depend on unqualified-search-registries.
    public override string ImageName => "docker.io/library/postgres";
    public override string NamePrefix => "pg";
}

/// <summary>
/// Shared container handling for everything that speaks PostgreSQL (PostgreSQL itself, TimescaleDB). Subclasses only
/// describe the image; SQL dialect, table tools and insights are shared.
/// </summary>
public abstract class PostgresFamilyProvider(IWslDockerRunner docker, ILogger log) : IDbmsProvider
{
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);

    public static bool IsFamily(string? key) =>
        key is PostgresProvider.ProviderKey or TimescaleDbProvider.ProviderKey;

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultImageTag { get; }
    /// <summary>Fully qualified image name without tag.</summary>
    public abstract string ImageName { get; }
    public abstract string NamePrefix { get; }
    public int DefaultPort => 5432;
    public string DefaultDatabase => "postgres";
    public string AdminUser => "postgres";

    // PostgreSQL 18+ images keep the data in a version-specific directory below /var/lib/postgresql.
    // Floating tags without a leading version (alpine, bookworm, trixie, latest) resolve to the newest major, so they count as 18+.
    protected virtual string DataMount(string tag)
    {
        var digits = new string(tag.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0) return "/var/lib/postgresql";
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major < 18
            ? "/var/lib/postgresql/data"
            : "/var/lib/postgresql";
    }

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
                DatabaseName = req.DatabaseName,
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

    public string PasswordEnvVar => "POSTGRES_PASSWORD";
    public string? DatabaseEnvVar => "POSTGRES_DB";

    public IReadOnlyList<string> BuildRunArgs(InstanceCreateRequest req, Guid id, string container, string volume) =>
    [
        "run", "-d", "--name", container,
        .. SharedContainerNetwork.RunArgs(req, container),
        "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
        // Loopback only: the password is the sole protection of this server.
        "-p", $"127.0.0.1:{req.HostPort}:5432",
        "-e", $"POSTGRES_PASSWORD={req.Password}",
        "-e", $"POSTGRES_DB={req.DatabaseName}",
        "-v", $"{volume}:{DataMount(req.ImageTag.Trim())}",
        $"{ImageName}:{req.ImageTag.Trim()}"
    ];

    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StartContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StopContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct) =>
        ContainerLifecycle.DeleteContainerAsync(docker, instance, deleteVolume, ct);

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) =>
        RunPsqlAsync(instance, instance.DatabaseName, sql, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword, string host = "127.0.0.1") =>
        $"Host={host};Port={i.HostPort};Database={i.DatabaseName};Username={i.Username};Password={(maskPassword ? "*****" : ConnectionStrings.Value(i.Password))};";

    private async Task<QueryResult> RunPsqlAsync(DbInstance instance, string database, string sql, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Password and options travel as environment variables and the SQL on stdin, so nothing needs shell quoting.
        // The server cancels the statement just before our own process timeout.
        var statementMs = Math.Max(5, (int)timeout.TotalSeconds - 5) * 1000;
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"PGPASSWORD={instance.Password}",
            "-e", "PGCONNECT_TIMEOUT=15",
            "-e", $"PGOPTIONS=-c statement_timeout={statementMs} -c client_min_messages=warning",
            instance.ContainerName,
            "psql", "-h", "127.0.0.1", "-U", instance.Username, "-d", database,
            "-X", "-v", "ON_ERROR_STOP=1", "-P", "pager=off", "-P", "null=NULL"
        ], timeout, sql.TrimEnd() + "\n", ct);

        var stdout = PsqlOutputConverter.ToSqlcmdStyle(r.StdOut);
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut, stdout, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            // The init-time server only listens on a Unix socket, so a TCP connection means the real server is up.
            last = await RunPsqlAsync(instance, instance.DatabaseName, "SELECT 1;", TimeSpan.FromSeconds(20), ct);
            if (last.IsSuccess) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return ("timed out waiting for PostgreSQL to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
    }

    private Task CleanupAsync(string container, string volume) =>
        ContainerLifecycle.CleanupAsync(docker, log, container, volume);
}
