using System.Diagnostics;

namespace DevDbStudio.Services.Dbms;

/// <summary>Apache CouchDB (document database with an HTTP API). Queries are HTTP requests sent with curl inside the container.</summary>
public sealed class CouchDbProvider(IWslDockerRunner docker, ILogger<CouchDbProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "couchdb";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);
    private const string BaseUrl = "http://127.0.0.1:5984";

    public string Key => ProviderKey;
    public string DisplayName => "CouchDB";
    public int DefaultPort => 5984;
    public string DefaultImageTag => "3";
    // CouchDB databases are created by requests (PUT /name), so the create form does not ask for one.
    public string DefaultDatabase => "couchdb";
    public string ImageName => "docker.io/library/couchdb";
    public string AdminUser => "admin";
    public string NamePrefix => "couch";

    public bool SupportsTables => false;
    public bool UsesDatabaseName => false;
    public string QueryLanguage => "HTTP requests (METHOD /path [JSON body], one per line)";
    public string SampleQuery => "GET /\nGET /_all_dbs\n\n" +
        "# Create a database and add a document\n" +
        "# PUT /customers\n" +
        "# POST /customers {\"name\": \"Anna\", \"age\": 30}\n" +
        "# GET /customers/_all_docs?include_docs=true";

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
                DatabaseName = "-",
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

            // A single node has no system databases until they are created; Fauxton and replication expect them.
            await ExecuteSqlAsync(instance, "PUT /_users\nPUT /_replicator", ct);
            return new InstanceCreateResult(true, instance, $"Instance created and {DisplayName} is ready. Fauxton, the web UI, is at http://127.0.0.1:{req.HostPort}/_utils");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(container, volume);
            throw;
        }
    }

    public string PasswordEnvVar => "COUCHDB_PASSWORD";
    public string? DatabaseEnvVar => null;

    public IReadOnlyList<string> BuildRunArgs(InstanceCreateRequest req, Guid id, string container, string volume) =>
    [
        "run", "-d", "--name", container,
        .. SharedContainerNetwork.RunArgs(req, container),
        "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
        // Loopback only: the admin password is the sole protection of this server.
        "-p", $"127.0.0.1:{req.HostPort}:5984",
        "-e", $"COUCHDB_USER={AdminUser}",
        "-e", $"COUCHDB_PASSWORD={req.Password}",
        "-v", $"{volume}:/opt/couchdb/data",
        $"{ImageName}:{req.ImageTag.Trim()}"
    ];

    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StartContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) =>
        ContainerLifecycle.StopContainerAsync(docker, instance, ct);

    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct) =>
        ContainerLifecycle.DeleteContainerAsync(docker, instance, deleteVolume, ct);

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string script, CancellationToken ct) =>
        RunRequestsAsync(instance, script, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword, string host = "127.0.0.1") =>
        $"http://{Uri.EscapeDataString(i.Username)}:{(maskPassword ? "*****" : Uri.EscapeDataString(i.Password))}@{host}:{i.HostPort}/";

    private async Task<QueryResult> RunRequestsAsync(DbInstance instance, string script, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (requests, error) = CouchDbScripts.Parse(script);
        if (error is not null) return new QueryResult(false, "", error, 2, sw.Elapsed);

        // The credentials, paths and bodies travel on stdin as a curl configuration, never on a command line.
        var r = await docker.RunAsync(
            ["exec", "-i", instance.ContainerName, "curl", "-K", "-"],
            timeout, CouchDbScripts.BuildCurlConfig(requests, BaseUrl, instance.Username, instance.Password), ct);

        var (text, allOk) = CouchDbScripts.Format(requests, r.StdOut);
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut && allOk, text, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RunRequestsAsync(instance, "GET /_up", TimeSpan.FromSeconds(15), ct);
            if (last.IsSuccess && last.StdOut.Contains("\"ok\"", StringComparison.Ordinal)) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return ("timed out waiting for the server to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
    }

    private Task CleanupAsync(string container, string volume) =>
        ContainerLifecycle.CleanupAsync(docker, log, container, volume);
}
