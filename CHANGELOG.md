# Changelog

Versioning follows [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`.

- **PATCH** (1.0.1): bug fixes only.
- **MINOR** (1.1.0): new features, backwards compatible.
- **MAJOR** (2.0.0): breaking changes (e.g. settings or data file format).

The version lives in one place: `<Version>` in `DevDbStudio.csproj`. Bump it with every change and add an entry here.
It is shown in the sidebar footer.

## Unreleased
- Clarified that application container access applies only to newly created instances.
- Setup checks Docker CLI/Engine 25.0+ and API 1.44+ for application container access. Unsupported or unreadable versions are rejected before creating application networks or enabling access; loopback-only instances and disabling access remain available.
- Added optional per-instance **Application container access** through the DNS-enabled `devdb-network` bridge. Newly created instances support enabling/disabling access after creation while preserving data and loopback access through separate private networks.
- Instance cards provide separate reveal/copy controls for host and application container connection strings, using each database's container name and internal port. Compose previews include the networks.
- Help documents application Compose configuration, linked beside each access setting. Existing instances are left unchanged and do not receive the networking toggle.
- Podman-managed WSL distros use their existing service socket to avoid WSLg runtime-directory and namespace mismatches when creating bridge-network containers.

## 2.0.0
- Create instance: the DBMS list is sorted alphabetically (SQL Server stays the preselected default); the Help page lists systems alphabetically, explains the generated password and how to write CouchDB queries.
- Added **CouchDB** (`docker.io/library/couchdb`, default tag `3`, admin user `admin`, password required, data in a volume). Fauxton is reachable at `/_utils` on the instance port; the system databases `_users` and `_replicator` are created on first start. The Query page takes HTTP requests, one per line (`GET /_all_dbs`, `PUT /customers`, `POST /customers {"name": "Anna"}`; further lines continue the JSON body) that are sent with curl inside the container, with the credentials passed on stdin. Replies are shown with their status code and indented JSON; Status shows database count and size. No table designer or table data. The create form has no database name field for CouchDB.

## 1.9.0
- Create instance: a valid random password is generated up front (with a note saying so) and a "Regenerate password" button creates a new one.

## 1.8.2
- Fixed the Instances "Get started" checklist layout (missing side padding and duplicated step numbers): it shared the `.steps` CSS class with the Help page and now uses its own `.checklist` class.

## 1.8.1
- Removed WSL branding from the general UI (sidebar, top bar, page subtitles, Status, Help overview). WSL is now mentioned only where it is functionally relevant: the Setup page on Windows.

## 1.8.0
- **Linux support.** On Linux the app runs `docker`/`podman` directly on the host; on Windows it still uses WSL2. The OS is detected automatically (`EngineHost`), nothing to configure.
- Setup page: native prerequisite checks on Linux (engine CLI, daemon access incl. `docker` group hint, rootless Podman hint, Compose); the WSL distro selector, keep-alive session and WSL wording are hidden/changed on Linux.
- Secrets still go through the process environment; `WSLENV` forwarding is Windows only.
- Verified by running a linux-x64 publish inside WSL against a real Docker engine. Tests added for `EngineHost`.
- Linux caveats: SQL Server images are x64 only; the self-contained publish needs `libicu`.

## 1.7.1
- AI helper files refreshed for DevDb Studio (shared lifecycle, compose preview, env-based secrets, legacy-name invariants).
- Added Copilot skills in `.github/skills/`: `add-dbms`, `add-page`, `verify`, `release`, `run-and-smoke-test`.

## 1.7.0
- Renamed the application from "DB Serve" to **DevDb Studio**: project (`DevDbStudio.csproj`), namespaces, tests (`tests/DevDbStudio.Tests`), JS namespace (`window.devDbStudio`), UI and docs.
- Intentionally unchanged for backward compatibility: data folder `%LocalAppData%\DbServeApp`, `dbapp-*` container names and `dbapp.*` labels, and `dbserve-*` browser storage keys.

## 1.6.0

- Create page: **Show compose preview** displays the exact container settings as a compose file. Port, image tag, password and database can be edited there and the form follows when the editor loses focus; edits the form cannot express are flagged as not used.
- Clear errors and warnings next to the preview: validation problems, duplicate name or port, floating tags, non-default tags, and port bindings that are not loopback-only.
- The preview is rendered from each provider's real `docker run` arguments (`IDbmsProvider.BuildRunArgs`), so it cannot drift from what is executed. The password is masked unless "Show" is pressed.
- Tests: 194 in total.

## 1.5.2

Remaining code-review findings.

- Connection strings (SQL Server, PostgreSQL, TimescaleDB, MySQL, MariaDB) quote passwords containing `;`, `"`, `'`, `=` or edge whitespace, so the copied string works.
- Redis: `(error) ...` replies are now reported as Failed instead of Success.
- MongoDB: creation waits for the image's "init process complete" log line before reporting ready, instead of pinging the temporary init-time server.
- A container-name collision on create no longer removes the existing container or volume.
- Status refresh no longer overwrites an instance that is being started/stopped/deleted, and updates only the status of the current record.
- Start/stop/delete/cleanup logic shared by all providers moved to `ContainerLifecycle`.
- CI: warnings are errors and NuGet packages are cached.
- Tests: 185 in total.

## 1.5.1

Fixes from a code review.

- Passwords are no longer part of the `wsl.exe`/`docker` command line: `-e NAME=value` options are moved into the process environment and forwarded with `WSLENV` (`ContainerEnvironment`), so they are not visible in process lists.
- `ProcessRunner` starts reading output before writing stdin (no deadlock with large scripts), and a timeout while writing stdin now kills the process and reports a timeout.
- Deleting an instance (and starting/stopping) finishes its bookkeeping even if the page is closed mid-operation, so no orphan records or volumes remain.
- PostgreSQL and TimescaleDB: floating tags without a leading version (`alpine`, `bookworm`, `latest`) use the PostgreSQL 18+ data directory layout.
- Engine/distro auto-detection no longer races with a change of choice (stale or empty results).
- Added tests for all of the above (168 in total).

## 1.5.0

- **Volumes can be deleted** on the Status page (Volumes tab): each volume that no container uses has a Delete button with confirmation. Use it to clean up data volumes that were kept when an instance was deleted.

## 1.4.0

- New **Help** page (`/help`, under a new "Help" section in the sidebar below "System"): step-by-step usage guide, a description of every page, a card per DBMS with its logo, defaults, client and example, connection addresses, data handling and troubleshooting.

## 1.3.1

- DBMS badges use the new SVG logos (replacing the PNG/placeholder icons) in a larger rounded white tile that stays readable in every theme.

## 1.3.0

- Added **TimescaleDB** (`docker.io/timescale/timescaledb`, default tag `latest-pg17`). It shares everything with PostgreSQL (psql, SQL Query page, table designer, table data, Status insights); timescaledb-internal schemas are hidden from table lists.
- Added **Redis** (`docker.io/library/redis`, default tag `8`, password required, append-only persistence). The Query page takes redis-cli commands, one per line (database `0`–`15` selectable); Status insights show clients, keyspaces and memory. No table designer or table data. The create form has no database name field for Redis.
- `PostgresProvider` now derives from a shared `PostgresFamilyProvider`; existing PostgreSQL container names are unchanged. Providers gained `UsesDatabaseName`.

## 1.2.0

- The WSL distro is now **auto-detected** (default `Wsl:DistroName` is `Auto`): the first distro with a working Docker or Podman is used, so Ubuntu, Debian, Fedora, `podman-machine-default` and others work without configuration. Docker Desktop's helper distros are ignored.
- The Setup page has a **WSL distro** selector next to the container engine selector; the choice is saved in `settings.json`. An explicit `Wsl:DistroName` still works.
- Setup checks and install hints no longer assume Ubuntu.
- 7 new tests (138 total).

## 1.1.0

- Added **MongoDB** support (`docker.io/library/mongo`, default tag `8`): create, start, stop and delete; connection string includes the database and `authSource=admin`.
- Query page runs mongosh JavaScript for MongoDB instances and shows the output as plain text; database list and Status page insights (connections, databases, size) work for MongoDB.
- Table designer and Table data skip MongoDB instances; providers gained `SupportsTables`, `QueryLanguage` and `SampleQuery`.
- The MongoDB sample query now also shows commented insert and find examples.
- 8 new tests (131 total).

## 1.0.1

- Added an automated xUnit test suite (`tests\DevDbStudio.Tests`, 123 tests).
- Fixed: saving the instance list could occasionally fail on Windows when the file was briefly locked; the save is now retried.

## 1.0.0

First versioned release: SQL Server, PostgreSQL, MariaDB and MySQL on Docker or Podman in WSL2; query, table designer and
table data tools; AVEVA-styled UI with light/dark theme; extended Status page with container details, database insights,
host resources and events.
