using System.Collections.Concurrent;
using System.Diagnostics;

namespace DevDbStudio.Services.Dbms;

public sealed class MssqlProvider(IWslDockerRunner docker, ILogger<MssqlProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "mssql";
    private const string Image = "mcr.microsoft.com/mssql/server";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);

    // sqlcmd location differs between images (mssql-tools vs mssql-tools18); detect once per container.
    private readonly ConcurrentDictionary<string, string> _sqlcmdPaths = new();

    public string Key => ProviderKey;
    public string DisplayName => "Microsoft SQL Server";
    public int DefaultPort => 1433;
    public string DefaultImageTag => "2022-latest";
    public string DefaultDatabase => "master";
    public string ImageName => Image;
    public string AdminUser => "sa";
    public string NamePrefix => "sql";

    public async Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct)
    {
        var errors = InstanceValidation.Validate(req);
        if (errors.Count > 0) return InstanceCreateResult.Fail(string.Join(" ", errors));

        var id = Guid.NewGuid();
        var shortId = id.ToString("N")[..8];
        var container = $"dbapp-mssql-{shortId}";
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
                Username = "sa",
                Password = req.Password,
                VolumeName = volume,
                ImageTag = tag,
                SharedNetwork = req.SharedNetwork,
                NetworkChangesSupported = true,
            };

            var ready = await WaitUntilReadyAsync(instance, ct);
            if (ready.Error is not null)
                return new InstanceCreateResult(true, instance, $"Container started, but SQL Server is not confirmed ready: {ready.Error}", ready.Detail);

            if (!string.Equals(req.DatabaseName, DefaultDatabase, StringComparison.OrdinalIgnoreCase))
            {
                var db = await RunSqlcmdAsync(instance, DefaultDatabase, $"IF DB_ID(N'{req.DatabaseName}') IS NULL CREATE DATABASE [{req.DatabaseName}];", QueryTimeout, ct);
                if (!db.IsSuccess)
                    return new InstanceCreateResult(true, instance, $"Container started, but database '{req.DatabaseName}' could not be created.", db.StdOut + db.StdErr);
            }
            return new InstanceCreateResult(true, instance, "Instance created and SQL Server is ready.");
        }
        catch (OperationCanceledException)
        {
            // Don't leave a half-created container behind when the user navigates away.
            await CleanupAsync(container, volume);
            throw;
        }
    }

    public string PasswordEnvVar => "MSSQL_SA_PASSWORD";
    public string? DatabaseEnvVar => null;

    public IReadOnlyList<string> BuildRunArgs(InstanceCreateRequest req, Guid id, string container, string volume) =>
    [
        "run", "-d", "--name", container,
        .. SharedContainerNetwork.RunArgs(req, container),
        "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
        // Loopback only: the SA password is the sole protection of this server.
        "-p", $"127.0.0.1:{req.HostPort}:1433",
        "-e", "ACCEPT_EULA=Y",
        "-e", $"MSSQL_SA_PASSWORD={req.Password}",
        "-v", $"{volume}:/var/opt/mssql",
        $"{Image}:{req.ImageTag.Trim()}"
    ];

    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StartContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StopContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct)
    {
        _sqlcmdPaths.TryRemove(instance.ContainerName, out _);
        return ContainerLifecycle.DeleteContainerAsync(docker, instance, deleteVolume, ct);
    }

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) =>
        RunSqlcmdAsync(instance, instance.DatabaseName, sql, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword, string host = "127.0.0.1") =>
        $"Server={host},{i.HostPort};Database={i.DatabaseName};User Id={i.Username};Password={(maskPassword ? "*****" : ConnectionStrings.Value(i.Password))};TrustServerCertificate=True;";

    private async Task<QueryResult> RunSqlcmdAsync(DbInstance instance, string database, string sql, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sqlcmd = await FindSqlcmdAsync(instance.ContainerName, ct);
        if (sqlcmd is null)
            return new QueryResult(false, "", $"sqlcmd was not found in container '{instance.ContainerName}' (looked in /opt/mssql-tools*/bin). Use an image tag that ships mssql-tools, such as 2022-latest.", -1, sw.Elapsed);

        // Password travels as an environment variable and the SQL on stdin, so neither shows up in a process list or needs shell quoting.
        var r = await docker.RunAsync(
        [
            "exec", "-i", "-e", $"SQLCMDPASSWORD={instance.Password}", instance.ContainerName,
            sqlcmd, "-S", "localhost", "-U", instance.Username, "-C", "-x", "-d", database, "-b", "-l", "15", "-t", QueryTimeoutSeconds(timeout), "-W", "-s", "\t"
        ], timeout, sql.TrimEnd() + "\nGO\n", ct);

        // sqlcmd reports its own query timeout on stdout and still exits 0.
        var timedOut = r.StdOut.Split('\n').Any(l => l.Trim() == "Timeout expired");
        var stderr = timedOut ? (r.StdErr + $"\nQuery timed out after {QueryTimeoutSeconds(timeout)} seconds and was cancelled.").Trim() : r.StdErr;
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut && !timedOut, r.StdOut, stderr, r.ExitCode, sw.Elapsed);
    }

    // Killing wsl.exe does not stop sqlcmd inside the container, so let sqlcmd cancel the query itself just before our own timeout.
    private static string QueryTimeoutSeconds(TimeSpan processTimeout) =>
        Math.Max(5, (int)processTimeout.TotalSeconds - 5).ToString();

    private async Task<string?> FindSqlcmdAsync(string container, CancellationToken ct)
    {
        if (_sqlcmdPaths.TryGetValue(container, out var cached)) return cached;
        // sort -r prefers mssql-tools18 over mssql-tools.
        var r = await docker.RunAsync(
            ["exec", container, "sh", "-c", "ls -1d /opt/mssql-tools*/bin/sqlcmd 2>/dev/null | sort -r | head -n 1"],
            TimeSpan.FromSeconds(20), ct: ct);
        var path = r.StdOut.Trim();
        if (r.ExitCode != 0 || !path.StartsWith("/opt/", StringComparison.Ordinal) || path.Contains('\n')) return null;
        return _sqlcmdPaths[container] = path;
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RunSqlcmdAsync(instance, DefaultDatabase, "SELECT 1;", TimeSpan.FromSeconds(20), ct);
            if (last.IsSuccess) return (null, "");
            if (last.ExitCode == -1 && last.StdErr.StartsWith("sqlcmd was not found", StringComparison.Ordinal))
                return ("sqlcmd is not available in this image.", last.StdErr);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return ("timed out waiting for SQL Server to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
    }

    private Task CleanupAsync(string container, string volume) =>
        ContainerLifecycle.CleanupAsync(docker, log, container, volume);
}
