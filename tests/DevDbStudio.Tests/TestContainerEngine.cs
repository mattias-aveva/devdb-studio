using DevDbStudio.Services;

namespace DevDbStudio.Tests;

internal sealed class TestContainerEngine(string executable = "podman") : IContainerEngine
{
    public EngineChoice Choice => executable == "docker" ? EngineChoice.Docker : EngineChoice.Podman;
    public string DistroChoice => "test";
    public event Action? Changed { add { } remove { } }
    public Task<string> GetExecutableAsync(CancellationToken ct = default) => Task.FromResult(executable);
    public Task<string> GetDistroAsync(CancellationToken ct = default) => Task.FromResult("test");
    public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["test"]);
    public void InvalidateDetection() { }
    public Task SetChoiceAsync(EngineChoice choice, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetDistroAsync(string choice, CancellationToken ct = default) => throw new NotSupportedException();
}
