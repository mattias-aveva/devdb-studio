using System.Text.Json;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Options;

namespace DevDbStudio.Services;

public enum EngineChoice { Auto, Docker, Podman }

/// <summary>Which WSL distro and container engine CLI (docker or podman) the app uses. Both can be auto-detected.</summary>
public interface IContainerEngine
{
    EngineChoice Choice { get; }
    /// <summary>"Auto" or the name of the WSL distro chosen by the user.</summary>
    string DistroChoice { get; }
    /// <summary>Raised after the choice changed or detection was forced to run again.</summary>
    event Action? Changed;
    /// <summary>The effective executable, "docker" or "podman". In Auto mode the result is detected and cached.</summary>
    Task<string> GetExecutableAsync(CancellationToken ct = default);
    /// <summary>The effective WSL distro. In Auto mode the first distro with a working engine is detected and cached.</summary>
    Task<string> GetDistroAsync(CancellationToken ct = default);
    /// <summary>Installed WSL distros that can host containers (Docker Desktop's internal distros excluded).</summary>
    Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct = default);
    /// <summary>Forgets the cached auto-detection result so the next call probes again.</summary>
    void InvalidateDetection();
    Task SetChoiceAsync(EngineChoice choice, CancellationToken ct = default);
    Task SetDistroAsync(string choice, CancellationToken ct = default);
}

public static class ContainerEngines
{
    public const string AutoDistro = "Auto";

    public static string DisplayName(string executable) => executable == "podman" ? "Podman" : "Docker";

    public static bool IsAuto(string? distro) =>
        string.IsNullOrWhiteSpace(distro) || distro.Equals(AutoDistro, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses `wsl -l -q` output. The default distro comes first; Docker Desktop's helper distros are not usable hosts.</summary>
    internal static IReadOnlyList<string> ParseDistros(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\0', '\uFEFF', ' ', '\r'))
            .Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed class ContainerEngine : IContainerEngine
{
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(15);

    private readonly WslOptions _opt;
    private readonly string _settingsPath;
    private readonly ILogger<ContainerEngine> _log;
    private readonly SemaphoreSlim _detectLock = new(1, 1);
    private volatile EngineChoice _choice;
    private volatile string _distroChoice;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private sealed record Detection(string Distro, string Exe, bool Healthy, DateTime At);

    // Replaced as a whole and read once per use; the generation lets a probe that started before a change be discarded.
    private volatile Detection? _detected;
    private int _generation;

    public ContainerEngine(IOptions<WslOptions> wsl, IOptions<StorageOptions> storage, ILogger<ContainerEngine> log)
    {
        _opt = wsl.Value;
        _log = log;
        var configured = storage.Value.InstancesFile;
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbServeApp")
            : Path.GetDirectoryName(Path.GetFullPath(configured)) ?? ".";
        _settingsPath = Path.Combine(dir, "settings.json");

        _choice = Enum.TryParse<EngineChoice>(_opt.ContainerEngine, true, out var fromConfig) ? fromConfig : EngineChoice.Auto;
        _distroChoice = ContainerEngines.IsAuto(_opt.DistroName) ? ContainerEngines.AutoDistro : _opt.DistroName.Trim();
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
                if (s is not null && Enum.TryParse<EngineChoice>(s.ContainerEngine, true, out var saved)) _choice = saved;
                if (s?.Distro is not null) _distroChoice = ContainerEngines.IsAuto(s.Distro) ? ContainerEngines.AutoDistro : s.Distro.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read {Path}; using configured container engine", _settingsPath);
        }
    }

    private sealed record Settings(string ContainerEngine, string? Distro = null);

    public EngineChoice Choice => _choice;
    public string DistroChoice => _distroChoice;
    public event Action? Changed;

    public async Task<string> GetExecutableAsync(CancellationToken ct = default)
    {
        switch (_choice)
        {
            case EngineChoice.Docker: return "docker";
            case EngineChoice.Podman: return "podman";
        }
        return (await EnsureDetectedAsync(ct)).Exe;
    }

    public async Task<string> GetDistroAsync(CancellationToken ct = default) =>
        !EngineHost.UsesWsl ? "" : ContainerEngines.IsAuto(_distroChoice) ? (await EnsureDetectedAsync(ct)).Distro : _distroChoice;

    public async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct = default)
    {
        if (!EngineHost.UsesWsl) return [];
        var r = await ProcessRunner.RunAsync("wsl.exe", ["-l", "-q"], TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 10)), ct);
        return r.ExitCode == 0 ? ContainerEngines.ParseDistros(r.StdOut) : [];
    }

    private async Task<(string Distro, string Exe)> EnsureDetectedAsync(CancellationToken ct)
    {
        if (TryCached(out var cached)) return cached;

        await _detectLock.WaitAsync(ct);
        try
        {
            if (TryCached(out cached)) return cached;
            var generation = Volatile.Read(ref _generation);
            var (distro, exe, healthy) = await DetectAsync(ct);
            // A choice made while probing makes this result stale; use it for this call only.
            if (generation == Volatile.Read(ref _generation))
                _detected = new Detection(distro, exe, healthy, DateTime.UtcNow);
            _log.LogInformation("Detected {Engine} (healthy: {Healthy}) on {Host}", exe, healthy, EngineHost.UsesWsl ? $"WSL distro {distro}" : "the local host");
            return (distro, exe);
        }
        finally { _detectLock.Release(); }
    }

    // A working engine stays cached until invalidated; a failed detection is retried after a short while.
    private bool TryCached(out (string Distro, string Exe) detected)
    {
        var d = _detected;
        if (d is not null && (d.Healthy || DateTime.UtcNow - d.At < FailureCacheTtl))
        {
            detected = (d.Distro, d.Exe);
            return true;
        }
        detected = default;
        return false;
    }

    private void Forget()
    {
        Interlocked.Increment(ref _generation);
        _detected = null;
    }

    public void InvalidateDetection()
    {
        Forget();
        Changed?.Invoke();
    }

    public async Task SetChoiceAsync(EngineChoice choice, CancellationToken ct = default)
    {
        _choice = choice;
        Forget();
        await SaveAsync(ct);
        Changed?.Invoke();
    }

    public async Task SetDistroAsync(string choice, CancellationToken ct = default)
    {
        _distroChoice = ContainerEngines.IsAuto(choice) ? ContainerEngines.AutoDistro : choice.Trim();
        Forget();
        await SaveAsync(ct);
        Changed?.Invoke();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            await WriteSettingsAsync(new Settings(_choice.ToString(), _distroChoice), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save settings to {Path}", _settingsPath);
        }
        finally { _settingsLock.Release(); }
    }

    private async Task WriteSettingsAsync(Settings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings), ct);
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Prefer a distro/engine pair that actually answers, then one that is merely installed.
    private async Task<(string Distro, string Exe, bool Healthy)> DetectAsync(CancellationToken ct)
    {
        var distros = !EngineHost.UsesWsl ? [""] : ContainerEngines.IsAuto(_distroChoice) ? await ListDistrosAsync(ct) : [_distroChoice];
        var fallbackDistro = distros.FirstOrDefault() ?? "Ubuntu";
        var engines = _choice switch { EngineChoice.Docker => new[] { "docker" }, EngineChoice.Podman => ["podman"], _ => ["docker", "podman"] };
        var timeout = TimeSpan.FromSeconds(Math.Max(_opt.ColdStartTimeoutSeconds, 15));

        foreach (var (arg, healthy) in new[] { ("info", true), ("--version", false) })
            foreach (var exe in engines)
                foreach (var distro in distros)
                {
                    var r = await EngineHost.RunAsync(distro, exe, [arg], timeout, ct);
                    if (r.ExitCode == 0) return (distro, exe, healthy);
                }
        return (fallbackDistro, engines[0], false);
    }
}
