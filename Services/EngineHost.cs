namespace DevDbStudio.Services;

/// <summary>
/// Where the container engine runs: inside a WSL distro on Windows, directly on the host on Linux.
/// Every engine or shell call goes through here so the rest of the app does not care about the OS.
/// </summary>
public static class EngineHost
{
    public static bool UsesWsl { get; } = OperatingSystem.IsWindows();

    /// <summary>Builds the process to start for <paramref name="exe"/> (docker, podman, sh, ...) with its arguments.</summary>
    public static (string File, string[] Args) Command(bool wsl, string distro, string exe, IEnumerable<string> args) =>
        wsl ? ("wsl.exe", ["-d", distro, "--exec", exe, .. args]) : (exe, [.. args]);

    public static async Task<ProcessResult> RunAsync(string distro, string exe, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, string? stdin = null, IReadOnlyDictionary<string, string>? env = null)
    {
        if (UsesPodmanMachineSocket(UsesWsl, distro, exe))
        {
            // WSLg can supply a different runtime directory and namespace than the machine's Podman service.
            var (idFile, idArgs) = Command(true, distro, "id", ["-u"]);
            var user = await ProcessRunner.RunAsync(idFile, idArgs, timeout, ct);
            if (user.ExitCode != 0 || user.TimedOut)
                return user with { StdErr = "Could not determine the Podman machine user. Check the WSL distro in Setup.\n" + user.StdErr };
            if (!uint.TryParse(user.StdOut.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid))
                return new(-1, "", "The Podman machine returned an invalid user ID. Check the WSL distro in Setup.", false);
            args = ["--remote", "--url", PodmanSocket(uid), .. args];
        }
        var (file, argv) = Command(UsesWsl, distro, exe, args);
        return await ProcessRunner.RunAsync(file, argv, timeout, ct, stdin, ForHost(env, UsesWsl));
    }

    internal static bool UsesPodmanMachineSocket(bool wsl, string distro, string exe) =>
        wsl && exe == "podman" && distro.StartsWith("podman-machine-", StringComparison.OrdinalIgnoreCase);

    internal static string PodmanSocket(uint uid) => uid == 0
        ? "unix:///run/podman/podman.sock"
        : $"unix:///run/user/{uid.ToString(System.Globalization.CultureInfo.InvariantCulture)}/podman/podman.sock";

    /// <summary>On Linux the engine CLI is a direct child, so the environment is passed as is; for WSL it is forwarded with WSLENV.</summary>
    public static IReadOnlyDictionary<string, string>? ForHost(IReadOnlyDictionary<string, string>? env, bool wsl)
    {
        if (env is null || env.Count == 0) return null;
        return wsl ? ContainerEnvironment.ForWsl(env) : env;
    }
}
