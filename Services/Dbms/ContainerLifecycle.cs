namespace DevDbStudio.Services.Dbms;

/// <summary>
/// Container start/stop/delete/cleanup steps that are identical for every DBMS. Providers keep only what differs
/// (image, environment, data path, readiness and query execution).
/// </summary>
internal static class ContainerLifecycle
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public static string Err(ProcessResult r) => r.StdErr.Length > 0 ? r.StdErr : r.StdOut;

    public static InstanceActionResult Result(ProcessResult r, string ok, string fail) =>
        r.ExitCode == 0 && !r.TimedOut ? new(true, ok) : new(false, r.TimedOut ? "The command timed out." : fail, Err(r));

    public static async Task<InstanceActionResult> CreatePrivateNetworkAsync(IWslDockerRunner docker, string container, CancellationToken ct)
    {
        // A separate persistent bridge allows sharing to be detached without losing published ports.
        var r = await docker.RunAsync(["network", "create", "--driver", "bridge", "--label", "dbapp.managed=true",
            SharedContainerNetwork.PrivateNetworkName(container)], ct: ct);
        return Result(r, "Private network created.", "Could not create the instance's private network. Check the engine and try again.");
    }

    public static async Task<InstanceActionResult> StartContainerAsync(IWslDockerRunner docker, DbInstance instance, CancellationToken ct)
    {
        var r = await docker.RunAsync(["start", instance.ContainerName], ct: ct);
        return Result(r, $"Started {instance.Name}.", "Container start failed.");
    }

    public static async Task<InstanceActionResult> StopContainerAsync(IWslDockerRunner docker, DbInstance instance, CancellationToken ct)
    {
        // Generous grace period so the server can flush and shut down cleanly.
        var r = await docker.RunAsync(["stop", "-t", "30", instance.ContainerName], StopTimeout, ct: ct);
        return Result(r, $"Stopped {instance.Name}.", "Container stop failed.");
    }

    public static async Task<InstanceActionResult> DeleteContainerAsync(IWslDockerRunner docker, DbInstance instance, bool deleteVolume, CancellationToken ct)
    {
        var rm = await docker.RunAsync(["rm", "-f", instance.ContainerName], StopTimeout, ct: ct);
        if (rm.ExitCode != 0 && !Err(rm).Contains("No such container", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removal failed.", Err(rm));

        if (instance.NetworkChangesSupported)
        {
            var network = await docker.RunAsync(["network", "rm", SharedContainerNetwork.PrivateNetworkName(instance.ContainerName)], ct: CancellationToken.None);
            if (network.ExitCode != 0 && !Err(network).Contains("no such network", StringComparison.OrdinalIgnoreCase)
                && !Err(network).Contains("not found", StringComparison.OrdinalIgnoreCase))
                return new(false, "Container removed, but its private network could not be deleted. Retry deletion.", Err(network));
        }

        if (!deleteVolume) return new(true, $"Deleted {instance.Name}. Data volume '{instance.VolumeName}' was kept.");

        // Not cancellable: once the container is gone the volume must follow, or it is orphaned.
        var vol = await docker.RunAsync(["volume", "rm", instance.VolumeName], ct: CancellationToken.None);
        if (vol.ExitCode != 0 && !Err(vol).Contains("No such volume", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removed, but the data volume could not be deleted.", Err(vol));
        return new(true, $"Deleted {instance.Name} and its data volume.");
    }

    /// <summary>Removes a half-created container and its volume. Never throws.</summary>
    public static async Task CleanupAsync(IWslDockerRunner docker, ILogger log, string container, string volume)
    {
        try
        {
            await docker.RunAsync(["rm", "-f", container], StopTimeout);
            await docker.RunAsync(["volume", "rm", volume]);
            var network = await docker.RunAsync(["network", "rm", SharedContainerNetwork.PrivateNetworkName(container)]);
            if (network.ExitCode != 0)
                log.LogWarning("Could not clean up private network for {Container}: {Error}", container, Err(network));
        }
        catch (Exception ex) { log.LogWarning(ex, "Cleanup of {Container} failed", container); }
    }

    /// <summary>
    /// Handles a failed <c>docker run</c>. A name conflict means the name belongs to another instance (short-id
    /// collision), so nothing may be cleaned up: that would delete somebody else's container and data.
    /// </summary>
    public static async Task<InstanceCreateResult> FailedRunAsync(
        IWslDockerRunner docker, ILogger log, ProcessResult run, string container, string volume)
    {
        if (IsNameConflict(run))
        {
            var network = await docker.RunAsync(["network", "rm", SharedContainerNetwork.PrivateNetworkName(container)]);
            if (network.ExitCode != 0)
                log.LogWarning("Could not clean up private network for {Container}: {Error}", container, Err(network));
            return InstanceCreateResult.Fail("Container name collision. Please try again.", Err(run));
        }
        await CleanupAsync(docker, log, container, volume);
        return InstanceCreateResult.Fail("Container run failed.", Err(run));
    }

    public static bool IsNameConflict(ProcessResult r) =>
        Err(r).Contains("is already in use", StringComparison.OrdinalIgnoreCase) &&
        Err(r).Contains("name", StringComparison.OrdinalIgnoreCase);
}
