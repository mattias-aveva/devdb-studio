# Architecture

```
 Browser (localhost:5190)
     │  Blazor Server circuit (SignalR)
     ▼
┌──────────────────────── DevDbStudio (Windows/Linux, .NET 10) ────────────────────────┐
│ Components/Pages ──► Components/Shared                                                │
│   Instances, InstanceCreate, Query, TableDesigner, TableData, Status, Setup           │
│        │                                                                              │
│        ▼  (interfaces, DI singletons registered in Program.cs)                        │
│  IInstanceManager ──► IDbmsProvider × 8 (mssql, postgres, timescaledb, mariadb, mysql, mongodb, redis, couchdb)    │
│  ITableDataService     IInstanceRepository ──► instances.json (%LOCALAPPDATA%)        │
│  IDbInsightService     IDockerService (Status snapshot cache)                         │
│  IPrerequisiteCheckService                                                            │
│        │                                                                              │
│        ▼                                                                              │
│  IWslDockerRunner ──► IContainerEngine (distro + docker/podman detection, settings.json)
│        │                                                                              │
│        ▼                                                                              │
│  ProcessRunner  ──►  EngineHost: wsl.exe -d <distro> --exec <docker|podman> <args>    │
│                      (Windows)   or  <docker|podman> <args> directly (Linux)          │
└────────────────────────────────────────┬──────────────────────────────────────────────┘
                                         ▼
          WSL2 Linux distro (Ubuntu, Debian, Podman machine, …) or the Linux host itself
                  └─ containers dbapp-<key>-<id>  ── 127.0.0.1:<port> ──► DB clients
                       (sqlcmd / psql / mariadb / mysql / mongosh run via `exec -i`)
```

## Boundaries
- **UI ↔ services:** pages depend only on interfaces and the models in `Services/Dbms/Models.cs`. No page runs processes.
- **Services ↔ host:** the only process boundary is `ProcessRunner`, reached through `EngineHost` (`wsl.exe` on Windows, the engine CLI directly on Linux; discrete arguments, timeouts, output cap of 2 MB). Never start `wsl.exe` or `docker` elsewhere. `IWslDockerRunner` adds the detected distro and engine.
- **Providers ↔ DBMS:** a provider owns image, ports, env vars, volumes and the client binary of one DBMS. Start/stop/delete/cleanup are shared in `Services\Dbms\ContainerLifecycle.cs`; new providers should call it rather than copy it. Each provider builds its `docker run` arguments in `BuildRunArgs`, which the create page's compose preview (`ComposePreview.cs`) renders, so keep container settings there and nowhere else. Queries run by `docker exec -i` inside the container with the script on stdin and credentials in env vars (`ContainerEnvironment` forwards them via `WSLENV`, keeping them off the command line); output is converted to a common tab-separated layout (SQL) or shown as text (MongoDB).
- **Persistence:** two JSON files (`instances.json`, `settings.json`); container state is the source of truth for running status (`InstanceManager.RefreshStatusAsync`).
- **Trust:** no authentication; loopback-only by middleware in `Program.cs`, containers bound to loopback.

## Key dependencies
- Runtime: .NET 10 / ASP.NET Core only; no third-party NuGet packages in the app.
- External: on Windows `wsl.exe` and a WSL2 distro with Docker Engine or Podman; on Linux Docker Engine or Podman on the host. DBMS images from `docker.io/library/*` and `mcr.microsoft.com/mssql/server`.
- Tests: xUnit, Microsoft.NET.Test.Sdk, xunit.runner.visualstudio.
- CI: GitHub Actions (`.github/workflows/ci.yml`) – build and test on ubuntu-latest.

## Cross-cutting behaviour
- Auto-detection (`ContainerEngine`): on Windows lists distros (`wsl -l -q`); probes `docker info` then `podman info`, caches a healthy result, retries failures after 15 s. On Linux there is no distro (empty name).
- `WslKeepAliveService` (Windows only) keeps a `wsl.exe … cat` session open so WSL does not stop the containers while the app runs.
- `DockerService` serialises fetches and caches fast (stats) and slow (images, volumes, disk) data separately for all circuits.
- Application container access is per instance (`SharedNetwork`), default off; there is no global setting. `InstanceManager` checks/creates the DNS-enabled `devdb-network` bridge through `ISharedContainerNetwork` for opted-in instances. Providers create a unique private bridge via `ContainerLifecycle`, and `BuildRunArgs` always includes that bridge plus `devdb-network` when selected. The private bridge keeps loopback publishing stable across later attach/detach operations. New records have `NetworkChangesSupported`; existing instances are left unchanged and do not support the networking toggle. Membership changes are serialized by the instance operation lock and saved only after engine success. Deletion/failed-creation cleanup removes the private network, never `devdb-network`. Compose previews reflect both networks; detailed application configuration is in Help.
- In Podman-managed WSL distros (`podman-machine-*`), `EngineHost` resolves the machine user ID and uses `podman --remote --url <user service socket>` with discrete arguments. This avoids WSLg runtime-directory/user-namespace mismatches when creating bridge-network containers. Other distros and native Linux retain direct execution.
