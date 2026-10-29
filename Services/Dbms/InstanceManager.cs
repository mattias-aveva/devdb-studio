using System.Collections.Concurrent;

namespace DevDbStudio.Services.Dbms;

public sealed record InstanceListResult(IReadOnlyList<DbInstance> Instances, string? Error);

/// <summary>Orchestrates repository, status detection and providers. Shared by all circuits.</summary>
public interface IInstanceManager
{
    IReadOnlyList<IDbmsProvider> Providers { get; }
    IDbmsProvider? GetProvider(string key);
    Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Runs <c>docker ps -a</c> once and maps each instance to Running/Stopped/NotFound.</summary>
    Task<InstanceListResult> RefreshStatusAsync(CancellationToken ct = default);
    Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct = default);
    Task<InstanceActionResult> StartAsync(Guid id, CancellationToken ct = default);
    Task<InstanceActionResult> StopAsync(Guid id, CancellationToken ct = default);
    Task<InstanceActionResult> SetSharedNetworkAsync(Guid id, bool enabled, CancellationToken ct = default);
    Task<InstanceActionResult> DeleteAsync(Guid id, bool deleteVolume, CancellationToken ct = default);
    Task<QueryResult> ExecuteSqlAsync(Guid id, string sql, CancellationToken ct = default);
    string BuildConnectionString(DbInstance instance, bool maskPassword);
    string? BuildNetworkConnectionString(DbInstance instance, bool maskPassword);
}

public sealed class InstanceManager(
    IInstanceRepository repo, IEnumerable<IDbmsProvider> providers, IWslDockerRunner docker, ILogger<InstanceManager> log,
    ISharedContainerNetwork network) : IInstanceManager
{
    private readonly IReadOnlyList<IDbmsProvider> _providers = providers.ToList();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public IReadOnlyList<IDbmsProvider> Providers => _providers;

    public IDbmsProvider? GetProvider(string key) =>
        _providers.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default) => repo.GetAllAsync(ct);

    public async Task<InstanceListResult> RefreshStatusAsync(CancellationToken ct = default)
    {
        var instances = await repo.GetAllAsync(ct);
        var ps = await docker.RunAsync(["ps", "-a", "--format", "{{.Names}}|{{.State}}"], ct: ct);
        if (ps.ExitCode != 0)
        {
            var err = ps.StdErr.Length > 0 ? ps.StdErr : ps.StdOut;
            return new InstanceListResult(instances.Select(i => i with { Status = InstanceStatus.Unknown }).ToList(), err);
        }

        var states = ps.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        var result = new List<DbInstance>();
        foreach (var i in instances)
        {
            var status = states.TryGetValue(i.ContainerName, out var state) ? MapState(state) : InstanceStatus.NotFound;
            var updated = i with { Status = status };
            if (status != i.Status) await PersistStatusAsync(i.Id, status, ct);
            result.Add(updated);
        }
        return new InstanceListResult(result, null);
    }

    // The status was observed before this call; a start/stop/delete in flight owns the record, so never overwrite it
    // with a stale value. Re-read the record so only the status field changes.
    private async Task PersistStatusAsync(Guid id, InstanceStatus status, CancellationToken ct)
    {
        if (!_locks.TryGetValue(id, out var gate)) gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None)) return;
        try
        {
            var current = await repo.GetAsync(id, ct);
            if (current is not null && current.Status != status)
                await repo.UpdateAsync(current with { Status = status }, ct);
        }
        finally { gate.Release(); }
    }

    private static InstanceStatus MapState(string state) => state switch
    {
        "running" or "restarting" or "paused" => InstanceStatus.Running,
        "exited" or "created" or "dead" or "stopped" or "configured" => InstanceStatus.Stopped,
        _ => InstanceStatus.Unknown
    };

    public async Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct = default)
    {
        var provider = GetProvider(req.DbmsKey);
        if (provider is null) return InstanceCreateResult.Fail($"Unknown DBMS '{req.DbmsKey}'.");

        // Serialized so two concurrent creates cannot claim the same port or name.
        await _createLock.WaitAsync(ct);
        try
        {
            var existing = await repo.GetAllAsync(ct);
            var name = req.Name?.Trim() ?? "";
            if (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                return InstanceCreateResult.Fail($"An instance named '{name}' already exists.");
            if (existing.Any(i => i.HostPort == req.HostPort))
                return InstanceCreateResult.Fail($"Port {req.HostPort} is already used by another instance.");

            if (req.SharedNetwork)
            {
                var validation = InstanceValidation.Validate(req);
                if (validation.Count > 0) return InstanceCreateResult.Fail(string.Join(" ", validation));
                var ready = await network.EnsureAsync(ct);
                if (!ready.Success) return InstanceCreateResult.Fail(ready.Message, ready.StdErr);
            }
            var result = await provider.CreateAsync(req with { Name = name }, ct);
            if (result.Success && result.Instance is not null)
            {
                await repo.AddAsync(result.Instance, CancellationToken.None);
                log.LogInformation("Created {Dbms} instance {Name} ({Container}) on port {Port}", provider.Key, name, result.Instance.ContainerName, req.HostPort);
            }
            var output = result.Message + " " + result.StdErr;
            if (!result.Success &&
                (output.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("address already in use", StringComparison.OrdinalIgnoreCase)))
                return InstanceCreateResult.Fail($"Port {req.HostPort} is already in use by another container or process outside DevDb Studio. Choose a different host port.", result.StdErr);
            return result;
        }
        finally { _createLock.Release(); }
    }

    public Task<InstanceActionResult> StartAsync(Guid id, CancellationToken ct = default) =>
        WithInstance(id, async (i, p) =>
        {
            if (i.SharedNetwork)
            {
                var ready = await network.EnsureAsync(ct);
                if (!ready.Success) return ready;
            }
            var r = await p.StartAsync(i, ct);
            if (r.Success) await repo.UpdateAsync(i with { Status = InstanceStatus.Running }, CancellationToken.None);
            return r;
        });

    public Task<InstanceActionResult> StopAsync(Guid id, CancellationToken ct = default) =>
        WithInstance(id, async (i, p) =>
        {
            var r = await p.StopAsync(i, ct);
            if (r.Success) await repo.UpdateAsync(i with { Status = InstanceStatus.Stopped }, CancellationToken.None);
            return r;
        });

    public Task<InstanceActionResult> SetSharedNetworkAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        WithInstance(id, async (i, _) =>
        {
            if (!i.NetworkChangesSupported)
                return new(false, "Network changes are available only for newly created instances. This container has not been changed.");
            var result = await network.SetMembershipAsync(i, enabled, ct);
            if (result.Success)
            {
                await repo.UpdateAsync(i with { SharedNetwork = enabled }, CancellationToken.None);
                log.LogInformation("Set application container access for {Container} to {Enabled}", i.ContainerName, enabled);
            }
            return result;
        });

    public Task<InstanceActionResult> DeleteAsync(Guid id, bool deleteVolume, CancellationToken ct = default) =>
        WithInstance(id, async (i, p) =>
        {
            var r = await p.DeleteAsync(i, deleteVolume, ct);
            if (r.Success)
            {
                // The container is already gone; the record must follow even if the circuit has been cancelled.
                await repo.RemoveAsync(id, CancellationToken.None);
                log.LogInformation("Deleted instance {Name} ({Container}), volume deleted: {Volume}", i.Name, i.ContainerName, deleteVolume);
            }
            return r;
        });

    public async Task<QueryResult> ExecuteSqlAsync(Guid id, string sql, CancellationToken ct = default)
    {
        var i = await repo.GetAsync(id, ct);
        if (i is null) return new QueryResult(false, "", "Instance not found.", -1, TimeSpan.Zero);
        var p = GetProvider(i.DbmsKey);
        if (p is null) return new QueryResult(false, "", $"No provider registered for '{i.DbmsKey}'.", -1, TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(sql)) return new QueryResult(false, "", "SQL is empty.", -1, TimeSpan.Zero);
        return await p.ExecuteSqlAsync(i, sql, ct);
    }

    public string BuildConnectionString(DbInstance instance, bool maskPassword) =>
        GetProvider(instance.DbmsKey)?.BuildConnectionString(instance, maskPassword) ?? "(no provider)";

    public string? BuildNetworkConnectionString(DbInstance instance, bool maskPassword) =>
        instance.SharedNetwork && GetProvider(instance.DbmsKey) is { } provider
            ? provider.BuildConnectionString(instance with { HostPort = provider.DefaultPort }, maskPassword, instance.ContainerName)
            : null;

    // One operation per instance at a time, so a double click can't start and stop concurrently.
    private async Task<InstanceActionResult> WithInstance(Guid id, Func<DbInstance, IDbmsProvider, Task<InstanceActionResult>> action)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var i = await repo.GetAsync(id);
            if (i is null) return new(false, "Instance not found.");
            var p = GetProvider(i.DbmsKey);
            if (p is null) return new(false, $"No provider registered for '{i.DbmsKey}'.");
            return await action(i, p);
        }
        finally { gate.Release(); }
    }
}
