using System.Text.Json;
using DevDbStudio.Services.Dbms;

namespace DevDbStudio.Services;

public interface ISharedContainerNetwork
{
    Task<InstanceActionResult> EnsureAsync(CancellationToken ct = default);
    Task<InstanceActionResult> SetMembershipAsync(DbInstance instance, bool enabled, CancellationToken ct = default);
}

public sealed class SharedContainerNetwork(IWslDockerRunner docker, ILogger<SharedContainerNetwork> log, IContainerEngine engine) : ISharedContainerNetwork
{
    public const string NetworkName = "devdb-network";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string PrivateNetworkName(string container) => container + "-network";

    public static IEnumerable<string> RunArgs(InstanceCreateRequest request, string container) =>
        request.SharedNetwork
            ? ["--network", PrivateNetworkName(container), "--network", NetworkName]
            : ["--network", PrivateNetworkName(container)];

    public async Task<InstanceActionResult> SetMembershipAsync(DbInstance instance, bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            var ready = await EnsureAsync(ct);
            if (!ready.Success) return ready;
        }
        var inspect = await docker.RunAsync(["inspect", "--format", "{{json .NetworkSettings.Networks}}", instance.ContainerName], ct: ct);
        if (inspect.ExitCode != 0 || inspect.TimedOut)
            return Failure("Could not inspect the instance network. Refresh its status and try again.", inspect);
        bool attached;
        try
        {
            using var json = JsonDocument.Parse(inspect.StdOut);
            attached = json.RootElement.TryGetProperty(NetworkName, out _);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not parse network membership for {Container}", instance.ContainerName);
            return new(false, "Could not read the instance network configuration. Check the container engine and try again.");
        }
        if (attached != enabled)
        {
            var change = await docker.RunAsync(["network", enabled ? "connect" : "disconnect", NetworkName, instance.ContainerName], ct: ct);
            if (change.ExitCode != 0 || change.TimedOut)
                return Failure("Could not change application container access. Refresh the instance and try again; start it first if your engine requires a running container.", change);
        }
        return new(true, enabled ? "Application container access enabled." : "Application container access disabled. Loopback access remains available.");
    }

    public async Task<InstanceActionResult> EnsureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await engine.GetExecutableAsync(ct) == "docker")
            {
                var compatible = await DockerNetworkCompatibility.CheckAsync(docker, ct);
                if (!compatible.Success) return Failure(compatible.Message, compatible.StdErr);
            }
            var networks = await docker.RunAsync(["network", "ls", "--format", "{{.Name}}"], ct: ct);
            if (networks.ExitCode != 0 || networks.TimedOut)
                return Failure("Could not list container networks. Check the container engine in Setup and try again.", networks);

            if (!networks.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(NetworkName, StringComparer.Ordinal))
            {
                var create = await docker.RunAsync(["network", "create", "--driver", "bridge", NetworkName], ct: ct);
                if (create.ExitCode != 0 || create.TimedOut)
                    return Failure("Could not create devdb-network. Check the container engine and network permissions, then try again.", create);
            }

            var inspect = await docker.RunAsync(["network", "inspect", NetworkName], ct: ct);
            if (inspect.ExitCode != 0 || inspect.TimedOut)
                return Failure("Could not inspect devdb-network. Check the container engine and try again.", inspect);
            try
            {
                using var json = JsonDocument.Parse(inspect.StdOut);
                var network = json.RootElement[0];
                var driver = network.TryGetProperty("Driver", out var dockerDriver) ? dockerDriver.GetString()
                    : network.TryGetProperty("driver", out var podmanDriver) ? podmanDriver.GetString() : null;
                var dnsDisabled = network.TryGetProperty("dns_enabled", out var dns) && dns.ValueKind == JsonValueKind.False;
                if (driver != "bridge" || dnsDisabled)
                    return Failure("devdb-network must be a bridge network with container DNS enabled. Rename or remove the conflicting network when it is safe, then try again.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
            {
                log.LogWarning(ex, "Could not parse application container network inspection");
                return new(false, "Could not read the devdb-network configuration. Check the container engine version and try again.");
            }
            return new(true, "Application container network devdb-network is ready.");
        }
        finally { _gate.Release(); }
    }

    private InstanceActionResult Failure(string message, ProcessResult? result = null) =>
        Failure(message, result is null ? "" : result.StdErr + result.StdOut);

    private InstanceActionResult Failure(string message, string detail)
    {
        log.LogWarning("Application container network setup failed: {Message}", message);
        return new(false, message, detail);
    }
}
