using DevDbStudio.Services.Dbms;

namespace DevDbStudio.Services;

internal static class DockerNetworkCompatibility
{
    internal const string VersionFormat = "{{.Client.Version}}|{{.Server.Version}}|{{.Client.APIVersion}}|{{.Server.APIVersion}}";
    internal const string Requirement = "Application container access requires Docker CLI and Engine 25.0 or newer with API 1.44 or newer.";
    internal const string Help = "Upgrade both the Docker CLI and Engine (or Docker Desktop), remove any DOCKER_API_VERSION override below 1.44, then rerun Setup. Loopback-only instances can still be used.";

    internal static async Task<InstanceActionResult> CheckAsync(IWslDockerRunner docker, CancellationToken ct)
    {
        var result = await docker.RunAsync(["version", "--format", VersionFormat], ct: ct);
        if (result.ExitCode != 0 || result.TimedOut)
            return new(false, result.TimedOut
                ? "The Docker version check timed out. Check the engine in Setup and try again."
                : "Could not check Docker compatibility for application container access. Check the engine in Setup and try again.",
                ContainerLifecycle.Err(result));

        var parts = result.StdOut.Trim().Split('|');
        if (parts.Length != 4 || parts.Any(p => !TryVersion(p, out _)))
            return new(false, "Could not read the Docker client, engine or API version. Upgrade Docker and rerun Setup.", result.StdOut);

        var minimumDocker = new Version(25, 0);
        var minimumApi = new Version(1, 44);
        var minimums = new[] { minimumDocker, minimumDocker, minimumApi, minimumApi };
        for (var index = 0; index < parts.Length; index++)
        {
            if (TryVersion(parts[index], out var version) && version < minimums[index])
                return new(false, Requirement + " " + Help,
                    $"Detected CLI {parts[0]}, Engine {parts[1]}, client API {parts[2]}, server API {parts[3]}.");
        }

        return new(true, $"Docker CLI {parts[0]} and Engine {parts[1]} support application container access.");
    }

    private static bool TryVersion(string text, out Version? version) =>
        Version.TryParse(text.Split('-', '+')[0], out version);
}
