# DevDb Studio

**Spin up a local database server in seconds, and manage it from your browser.**

DevDb Studio is a local web app that creates and manages database servers as Docker or Podman containers: on Windows inside WSL2, or directly on Linux. No Docker Desktop, no hand-written `docker run` commands.

[![CI](https://github.com/mattias-aveva/devdb-studio/actions/workflows/ci.yml/badge.svg)](https://github.com/mattias-aveva/devdb-studio/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)
![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey)

## Why

Developers constantly need a throw-away SQL Server, PostgreSQL or Redis to try something out. Setting one up means remembering image names, ports, volumes and client tools, and cleaning up afterwards. DevDb Studio wraps all of that in one UI: pick a DBMS, click create, run queries, generate test data, delete it when you are done.

It is for developers who work against local databases. It is **not** a production tool (see [Limitations](#limitations)).

## Demo

![DevDb Studio screenshot](docs/screenshot.png)

*Create and manage database containers from the browser.*

## Features

- **One-click instances**: create, start, stop and delete SQL Server, PostgreSQL, TimescaleDB, MariaDB, MySQL, MongoDB, Redis and CouchDB, with copyable connection strings and a compose preview.
- **Built-in query editor**: run SQL (or mongosh / redis-cli commands) with Ctrl+Enter, browse results as tables, keep history and snippets, export to CSV or JSON.
- **Table designer**: design a table visually and get a ready-to-run `CREATE TABLE` script for the right SQL dialect.
- **Realistic test data**: fill tables with 1–10,000 random rows that respect column names, types, keys and unique indexes. Clear them again with one click.
- **Live status**: engine, container and host statistics, logs, inspect and container administration.
- **Guided setup**: a prerequisite checker explains what is missing and gives the exact fix commands.
- **Light and dark theme**, following your OS preference.

### Supported databases

| DBMS | Query | Table designer & data | Notes |
|------|:-----:|:---------------------:|-------|
| Microsoft SQL Server | `sqlcmd` | ✅ | |
| PostgreSQL | `psql` | ✅ | |
| TimescaleDB | `psql` | ✅ | PostgreSQL features |
| MariaDB | `mariadb` | ✅ | |
| MySQL | `mysql` | ✅ | |
| MongoDB | `mongosh` | – | JavaScript, plain-text output |
| Redis | `redis-cli` | – | One command per line |
| CouchDB | `curl` (HTTP) | – | `METHOD /path [JSON body]` per line; Fauxton at `/_utils` |

## Quickstart

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker or Podman inside WSL2 (Windows) or on the host (Linux).

```powershell
git clone https://github.com/mattias-aveva/devdb-studio.git
cd devdb-studio
dotnet run
```

Open <http://localhost:5190>, check the **Setup** page, then go to **Instances → Create**.

## Installation

### Requirements

| | Windows | Linux |
|---|---|---|
| Container engine | Docker Engine + Compose plugin, or Podman, installed **inside** a WSL2 distro (any distro, auto-detected) | Docker or Podman on the host; your user must be allowed to use it (`docker` group or rootless Podman) |
| WSL | WSL2 required | Not used |
| Runtime | .NET 10 SDK | .NET 10 SDK (self-contained builds need `libicu`) |

The Setup page verifies all of this at runtime.

### Run from source

```powershell
dotnet run
```

### Publish a self-contained build

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

Replace the runtime identifier (for example `win-x64`) to target another platform.

## Usage

1. **Setup** (`/setup`): confirm WSL, the distro and the container engine are healthy. Distro and engine default to auto-detect and can be pinned here.
2. **Instances** (`/instances/create`): choose a DBMS, tag, port, password and database name. Review or edit the generated compose file, then create.
3. **Query** (`/query`): select an instance and run statements.

   ```sql
   CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(100));
   INSERT INTO customers VALUES (1, 'Anna');
   SELECT * FROM customers;
   ```

   MongoDB: `db.customers.insertOne({ name: "Anna" })`. Redis: `SET greeting "Hello"`. CouchDB: `PUT /customers` then `POST /customers {"name": "Anna"}`.
4. **Tables** (`/tables`) and **Table data** (`/data`): design tables and fill or clear them.
5. **Status** (`/status`): watch resources and administer containers.
6. **Help** (`/help`): per-DBMS defaults, connection addresses and troubleshooting.

Connect from your own tools with `127.0.0.1` (not `localhost`, which may resolve to IPv6): `127.0.0.1,<port>` for SQL Server, `127.0.0.1:<port>` for the others. Published host ports bind to loopback only.

### Application container access

Choose **Application container access** on **Create instance** for each database your application containers need. It defaults to off. The instance card lets you enable or disable access later without recreating the container or changing its data or loopback access. DevDb Studio creates/checks the DNS-enabled `devdb-network` bridge when needed. There is no global setting.

With Docker, application container access requires **Docker CLI and Engine 25.0+**, using **API 1.44+**, because initial creation connects to two networks. Setup reports compatibility, and the same check runs before application network setup or enabling access. Upgrade Docker (or Docker Desktop) and remove any `DOCKER_API_VERSION` override below 1.44 if the check fails. Loopback-only instances remain available with older Docker versions; disabling application container access is not blocked. Podman is not subject to this Docker version requirement.

On **Instances**, copy **Host connection (loopback)** for host applications or **Application container connection (devdb-network)** for applications on that network. The latter appears only when access is enabled and uses the unique container name and the DBMS's internal port, not its published host port. Multiple MariaDB instances can therefore all use internal port 3306 without conflicts.

Join application services to the external network in each Compose project:

```yaml
services:
  app:
    image: your-application-image
    networks:
      - default
      - devdb-network
networks:
  default: {}
  devdb-network:
    external: true
    name: devdb-network
```

Applications and database containers must use the same container engine, user and (on Windows) WSL distro. Any container on `devdb-network` can attempt to connect to enabled databases; database credentials are still required. Attach only trusted applications. This does not expose database ports to your LAN.

For Podman-managed WSL distros (`podman-machine-*`), DevDb Studio uses the machine's existing Podman service socket so bridge networking uses the same runtime and network namespace as Podman on Windows. The Podman machine/service must be running; other WSL distros and native Linux continue to use their engine directly.

New instances each retain a private bridge network named `<container-name>-network`, even when application container access is off, so access can be toggled safely on Docker and Podman. Disabling access detaches only that instance from `devdb-network` and removes its application container connection string; active application-container connections are interrupted. Host access remains available. If an engine requires the container to be running for network changes, start it and retry.

Application container access is available only for newly created instances. Existing instances are left unchanged and do not have the new toggle. Deleting a new instance removes its private network but never deletes `devdb-network`. If you already have a network named `devdb-network`, it must use the bridge driver with container DNS enabled. Full application setup instructions are available under **Help → Application container access**, linked as **Connecting from application containers** beside each checkbox.

## Configuration

Settings live in `appsettings.json`. Choices made on the Setup page are saved in `settings.json` next to the instances file and take precedence.

| Key | Default | Description |
|-----|---------|-------------|
| `Wsl:DistroName` | `Auto` | WSL distro hosting the engine. `Auto` picks the first distro with a working engine |
| `Wsl:ContainerEngine` | `Auto` | `Auto`, `Docker` or `Podman` |
| `Wsl:CommandTimeoutSeconds` | `10` | Default timeout for `wsl.exe` commands |
| `Wsl:ColdStartTimeoutSeconds` | `60` | Timeout while WSL is starting |
| `Wsl:KeepAlive` | `true` | Keep a `wsl.exe` session open so WSL does not shut down and stop your containers |
| `Storage:InstancesFile` | `%LocalAppData%\DbServeApp\instances.json` | Where instances are persisted |
| `Security:AllowRemoteAccess` | `false` | Allow non-loopback clients (not recommended) |

Any key can also be set through an environment variable, for example `Storage__InstancesFile`.

## Architecture

```
Browser ── Blazor Server (Components/Pages, Shared)
              │
              ├─ InstanceManager ── IDbmsProvider (one per DBMS) ── ContainerLifecycle
              ├─ TableDataService / DbInsightService / DockerService
              ▼
        IWslDockerRunner → EngineHost → ProcessRunner → wsl.exe → docker | podman
                                                        (Linux: docker | podman directly)
```

- `Services/`: engine and WSL plumbing, prerequisite checks, Status page data.
- `Services/Dbms/`: the provider pattern (`IDbmsProvider`), script generation, output parsers and the JSON instance store.
- `Components/`: pages, shared components and layout. Styling in `wwwroot/app.css`.
- State is a JSON file; the app has no database of its own.
- Arguments are passed as discrete tokens (no shell), secrets via environment variables, scripts via stdin.

See [ARCHITECTURE.md](ARCHITECTURE.md) for details and [AI.md](AI.md) for conventions.

## Tests

```powershell
dotnet test tests\DevDbStudio.Tests
dotnet build DevDbStudio.csproj -warnaserror
dotnet format DevDbStudio.csproj --include <changed files>
```

The xUnit suite covers validation, script generation, output parsing, engine JSON parsing and the instance store. It needs neither WSL nor a container engine. If the app is running, add `-o "$env:TEMP\dbs-test-out"` to avoid the locked `bin\Debug`.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Setup shows WSL or distro missing | Install WSL2 and a distro (`wsl --install`), then recheck |
| No container engine found | Install Docker Engine (with Compose) or Podman inside the distro / on the host |
| Permission denied on Linux | Add your user to the `docker` group, or use rootless Podman |
| Containers stop when the app is idle (Windows) | Keep `Wsl:KeepAlive` enabled |
| Cannot connect to `localhost:<port>` | Use `127.0.0.1` instead; ports are bound to IPv4 loopback |
| Instance shows "not found" after switching engine | Containers belong to the engine that created them; switch back |
| Port already in use | Choose another port between 1024 and 65535 |
| Table data fails on a `CHECK` constraint | Constraints are unknown to the generator; insert those rows manually |
| Remote machine cannot open the UI | Intentional. See `Security:AllowRemoteAccess` and the [warning](#security) |

## Limitations

- Local development only; passwords are stored in plain text.
- No authentication or multi-user support.
- Does not install WSL or Docker for you.
- MongoDB, Redis and CouchDB have no table designer or table data generation.
- Podman statistics on the Status page are best-effort.
- Result grids are simple; there is no advanced data editing.

## Roadmap

- [ ] Demo GIF in this README
- [ ] More DBMS providers (see the `add-dbms` skill in `.github/skills`)
- [ ] Richer result grids

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first. In short: every PR must pass CI, include tests, bump `<Version>` in `DevDbStudio.csproj`, add a [CHANGELOG.md](CHANGELOG.md) entry and update this README when behaviour or configuration changes. Use file-scoped namespaces, async end to end with `CancellationToken`, and no secrets in code or logs.

Adding a new database? Implement `IDbmsProvider`, register it in `Program.cs` and follow `.github/skills/add-dbms/SKILL.md`.

## Security

- The app only serves loopback clients and rejects cross-origin requests by default.
- Instance passwords are stored in plain text in `instances.json` and are visible in `docker inspect`. Use throw-away credentials.
- The Query and Tables pages run arbitrary SQL by design, and Table data can delete all rows of a table.

To report a vulnerability, please email [mattias.dahlgren@aveva.com](mailto:mattias.dahlgren@aveva.com) privately instead of opening a public issue.

## Versioning

Semantic versioning. The current version is in `DevDbStudio.csproj`, shown in the sidebar footer and logged in [CHANGELOG.md](CHANGELOG.md).

## License

No license file is included yet, so all rights are reserved by default. Add a `LICENSE` file (for example MIT) and reference its SPDX identifier here.

## Credits

Built by Mattias Dahlgren. Database logos belong to their respective owners.
