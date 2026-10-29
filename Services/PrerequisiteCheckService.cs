using Microsoft.Extensions.Options;

namespace DevDbStudio.Services;

public enum CheckStatus { Unknown, Pass, Fail }

public sealed class WslOptions
{
    public string DistroName { get; set; } = "Auto";
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int ColdStartTimeoutSeconds { get; set; } = 60;
    // Holds a wsl.exe session open so WSL doesn't shut down the VM (and kill containers) while idle.
    public bool KeepAlive { get; set; } = true;
    // Auto, Docker or Podman. A choice saved from the Setup page (settings.json) overrides this.
    public string ContainerEngine { get; set; } = "Auto";
}

public sealed record CheckResult(
    string CheckId, string Title, CheckStatus Status, string Command,
    string StdOut, string StdErr, string WhyItMatters, string HelpMarkdown);

public interface IPrerequisiteCheckService
{
    Task<IReadOnlyList<CheckResult>> RunChecksAsync(CancellationToken ct = default);
}

public sealed class PrerequisiteCheckService(IOptions<WslOptions> options, IContainerEngine engine, IWslDockerRunner docker) : IPrerequisiteCheckService
{
    private const string Wsl = "wsl.exe";
    private readonly WslOptions _opt = options.Value;
    private readonly IContainerEngine _engine = engine;

    public async Task<IReadOnlyList<CheckResult>> RunChecksAsync(CancellationToken ct = default)
    {
        if (!EngineHost.UsesWsl) return await RunNativeChecksAsync(ct);

        var results = new List<CheckResult>();
        var autoDistro = ContainerEngines.IsAuto(_engine.DistroChoice);
        var wanted = autoDistro ? "Ubuntu" : _engine.DistroChoice;

        var wsl = await Run("wsl", "WSL available", "The container engine for this app runs inside WSL2, which must be installed.",
            ["--status"], r => r.ExitCode == 0,
            """
            1. Open **PowerShell as Administrator**.
            2. Run: `wsl --install`
            3. Reboot Windows when prompted.
            """, ct);
        results.Add(wsl);

        var distro = await Run("distro", autoDistro ? "WSL distro installed" : $"{wanted} distro installed",
            autoDistro ? "Docker or Podman runs inside a WSL distribution; any Linux distro will do." : $"Docker or Podman is installed in the '{wanted}' WSL distribution.",
            ["-l", "-q"],
            r => r.ExitCode == 0 && (autoDistro
                ? ContainerEngines.ParseDistros(r.StdOut).Count > 0
                : ContainerEngines.ParseDistros(r.StdOut).Any(l => string.Equals(l, wanted, StringComparison.OrdinalIgnoreCase))),
            $"""
            1. Open PowerShell.
            2. Run: `wsl --install -d {wanted}`
            3. Launch {wanted} once and create your Linux user.
            4. Verify with `wsl -l -q`.
            """, ct, dependsOn: wsl);
        results.Add(distro);

        // Re-detect on every run so a newly installed or started distro/engine is picked up.
        _engine.InvalidateDetection();
        var d = distro.Status == CheckStatus.Pass ? await _engine.GetDistroAsync(ct) : wanted;
        var exe = distro.Status == CheckStatus.Pass ? await _engine.GetExecutableAsync(ct) : "docker";
        var name = ContainerEngines.DisplayName(exe);
        var podman = exe == "podman";

        // Starting the WSL VM can take far longer than a normal command; absorb that once so later checks don't time out.
        if (distro.Status == CheckStatus.Pass)
            await ProcessRunner.RunAsync(Wsl, ["-d", d, "--exec", "true"], TimeSpan.FromSeconds(_opt.ColdStartTimeoutSeconds), ct);

        var cli = await Run("engine-cli", $"{name} CLI installed", $"The app uses the {exe} command to create database containers.",
            ["-d", d, "--", exe, "--version"], r => r.ExitCode == 0,
            podman
                ? $"""
                  1. Open {d}: `wsl -d {d}`
                  2. Install Podman with your distro's package manager, e.g. `sudo apt-get update && sudo apt-get install -y podman` (Debian/Ubuntu) or `sudo dnf install -y podman` (Fedora)
                  3. Verify: `podman --version`
                  4. Or install Docker instead and pick it under **Container engine** above.
                  """
                : $"""
                  1. Open {d}: `wsl -d {d}`
                  2. Install Docker Engine: `curl -fsSL https://get.docker.com | sudo sh`
                  3. Verify: `docker --version`
                  4. Or install Podman instead (`sudo apt-get install -y podman`) and pick it under **Container engine** above.
                  """, ct, dependsOn: distro);
        results.Add(cli);

        var daemon = await Run("engine-daemon",
            podman ? "Podman working" : "Docker daemon reachable",
            podman ? "Podman is daemonless, but `podman info` must succeed for your user before containers can be created."
                   : "Containers can only be created when the Docker daemon is running and accessible.",
            ["-d", d, "--", exe, "info"], r => r.ExitCode == 0,
            podman
                ? """
                  1. Run `podman info` in WSL and read the error.
                  2. Rootless Podman needs a working user session; try `wsl --shutdown` from PowerShell and reopen the distro.
                  3. Verify: `podman info`
                  """
                : """
                  1. Start the daemon: `sudo service docker start` (or `sudo systemctl start docker` if systemd is enabled).
                  2. Verify: `docker info`
                  """, ct, dependsOn: cli,
            failHelp: r => !podman && IsPermissionDenied(r)
                ? $"""
                  1. Your user is not allowed to access /var/run/docker.sock (permission denied). Add your user to the docker group: `sudo usermod -aG docker $USER`
                  2. Restart WSL from PowerShell: `wsl --shutdown`
                  3. Reopen {d} and verify: `docker info`
                  """
                : null);
        results.Add(daemon);
        if (!podman) results.Add(await CheckDockerNetworkAsync(daemon, ct));

        results.Add(await Run("compose", $"{name} Compose available", "Compose is used to define and start database stacks.",
            ["-d", d, "--", exe, "compose", "version"], r => r.ExitCode == 0,
            podman
                ? $"""
                  1. Open {d}: `wsl -d {d}`
                  2. Install a compose provider: `sudo apt-get update && sudo apt-get install -y podman-compose`
                  3. Verify: `podman compose version`
                  """
                : $"""
                  1. Open {d}: `wsl -d {d}`
                  2. Install the plugin: `sudo apt-get update && sudo apt-get install -y docker-compose-plugin`
                  3. Verify: `docker compose version`
                  """, ct, dependsOn: cli));

        return results;
    }

    // Linux: the engine runs on the host itself, so there is no WSL or distro to check.
    private async Task<IReadOnlyList<CheckResult>> RunNativeChecksAsync(CancellationToken ct)
    {
        _engine.InvalidateDetection();
        var exe = await _engine.GetExecutableAsync(ct);
        var name = ContainerEngines.DisplayName(exe);
        var podman = exe == "podman";
        var results = new List<CheckResult>();

        var cli = await Run("engine-cli", $"{name} CLI installed", $"The app uses the {exe} command to create database containers.",
            ["--version"], r => r.ExitCode == 0,
            podman
                ? """
                  1. Install Podman with your package manager, e.g. `sudo apt-get install -y podman` (Debian/Ubuntu) or `sudo dnf install -y podman` (Fedora).
                  2. Verify: `podman --version`
                  3. Or install Docker instead and pick it under **Container engine** above.
                  """
                : """
                  1. Install Docker Engine: `curl -fsSL https://get.docker.com | sudo sh`
                  2. Verify: `docker --version`
                  3. Or install Podman instead (`sudo apt-get install -y podman`) and pick it under **Container engine** above.
                  """, ct, file: exe);
        results.Add(cli);

        var daemon = await Run("engine-daemon",
            podman ? "Podman working" : "Docker daemon reachable",
            podman ? "Podman is daemonless, but `podman info` must succeed for your user before containers can be created."
                   : "Containers can only be created when the Docker daemon is running and accessible.",
            ["info"], r => r.ExitCode == 0,
            podman
                ? """
                  1. Run `podman info` in a terminal and read the error.
                  2. Rootless Podman needs subuid/subgid entries for your user (`grep $USER /etc/subuid`); add them with `sudo usermod --add-subuids 100000-165535 --add-subgids 100000-165535 $USER`, then run `podman system migrate`.
                  3. Verify: `podman info`
                  """
                : """
                  1. Start the daemon: `sudo systemctl enable --now docker`
                  2. Verify: `docker info`
                  """, ct, dependsOn: cli, file: exe,
            failHelp: r => !podman && IsPermissionDenied(r)
                ? """
                  1. Your user is not allowed to access /var/run/docker.sock (permission denied). Add your user to the docker group: `sudo usermod -aG docker $USER`
                  2. Log out and back in (or run `newgrp docker`), then verify: `docker info`
                  """
                : null);
        results.Add(daemon);
        if (!podman) results.Add(await CheckDockerNetworkAsync(daemon, ct));

        results.Add(await Run("compose", $"{name} Compose available", "Compose is used to define and start database stacks.",
            ["compose", "version"], r => r.ExitCode == 0,
            podman
                ? """
                  1. Install a compose provider: `sudo apt-get install -y podman-compose`
                  2. Verify: `podman compose version`
                  """
                : """
                  1. Install the plugin: `sudo apt-get install -y docker-compose-plugin`
                  2. Verify: `docker compose version`
                  """, ct, dependsOn: cli, file: exe));

        return results;
    }

    internal async Task<CheckResult> CheckDockerNetworkAsync(CheckResult daemon, CancellationToken ct)
    {
        const string title = "Docker application container access compatible";
        const string command = "docker version --format \"" + DockerNetworkCompatibility.VersionFormat + "\"";
        if (daemon.Status != CheckStatus.Pass)
            return new("docker-network-version", title, CheckStatus.Unknown, command, "",
                $"Skipped because '{daemon.Title}' did not pass.", DockerNetworkCompatibility.Requirement, "");

        var result = await DockerNetworkCompatibility.CheckAsync(docker, ct);
        return new("docker-network-version", title, result.Success ? CheckStatus.Pass : CheckStatus.Fail,
            command, result.Message, result.StdErr, DockerNetworkCompatibility.Requirement,
            result.Success ? "" : DockerNetworkCompatibility.Help);
    }

    private static bool IsPermissionDenied(ProcessResult r) =>
        (r.StdErr + r.StdOut).Contains("permission denied", StringComparison.OrdinalIgnoreCase);

    private async Task<CheckResult> Run(
        string id, string title, string why, string[] args, Func<ProcessResult, bool> pass, string help,
        CancellationToken ct, CheckResult? dependsOn = null, Func<ProcessResult, string?>? failHelp = null, string file = Wsl)
    {
        var command = $"{file} {string.Join(' ', args)}";
        // Skip dependent checks when a prerequisite failed; their result would be meaningless.
        if (dependsOn is { Status: not CheckStatus.Pass })
            return new CheckResult(id, title, CheckStatus.Unknown, command, "",
                $"Skipped because '{dependsOn.Title}' did not pass.", why, "");

        var r = await ProcessRunner.RunAsync(file, args, TimeSpan.FromSeconds(_opt.CommandTimeoutSeconds), ct);
        if (pass(r))
            return new CheckResult(id, title, CheckStatus.Pass, command, r.StdOut, r.StdErr, why, "");

        return new CheckResult(id, title, CheckStatus.Fail, command, r.StdOut, r.StdErr, why, failHelp?.Invoke(r) ?? help);
    }
}
