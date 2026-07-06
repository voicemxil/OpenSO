---
name: db-migrations
description: Adding or changing OpenSO server database schema and data-access code — manifest-driven SQL migrations, the fso_db_changes hash check, and the Dapper DA pattern with dual MySQL/SQLite support. Use for any change under TSOClient/FSO.Server.Database or anything that needs new persistent server-side data.
---

# Database migrations & DAL (OpenSO)

## How migrations work

- Migration scripts: `TSOClient/FSO.Server.Database/DatabaseScripts/` — base table scripts at the
  root, incremental changes in `changes/NNNN_description.sql` (currently up to 0031).
- `DatabaseScripts/manifest.json` is the source of truth: each entry is
  `{ "id": "<fresh GUID>", "script": "changes/NNNN_name.sql", "idempotent": bool, "requires": ["<GUID>", ...] }`.
  Order and dependencies come from the manifest, not filenames.
- `Management/DbChangeTool.cs` applies them: it compares manifest entries against the
  `fso_db_changes` table, runs missing scripts in a RepeatableRead transaction, and records
  `(id, filename, date, hash)`.
- **The hash check is why you never edit a shipped script**: each script is MD5-hashed with
  whitespace normalized; any edit flags the change as MODIFIED on existing databases. To alter
  something already shipped, add a NEW change script.
- Applied via `dotnet FSO.Server.Core.dll db-init` (interactive diff + Y/n prompt). The docker
  entrypoint runs `yes y | ... db-init` on every container start, so merged migrations roll out
  automatically on deploy/update.

## Adding a schema change — recipe

1. Create `DatabaseScripts/changes/00XX_short_name.sql` (next number). Write MariaDB/MySQL-compatible
   SQL; look at neighbors like `0031_global_cooldowns.sql` for style.
2. Generate a fresh GUID and append an entry to `manifest.json`. Set `requires` to the GUID(s) of
   any change/base table it depends on (e.g. avatar-table dependents require the avatars entry).
3. Prefer idempotent SQL (`CREATE TABLE IF NOT EXISTS`, guarded `ALTER`) and set `idempotent`
   accordingly.
4. Ensure the .sql is included in build output like its neighbors (check
   `FSO.Server.Database.csproj` if `db-init` can't find it — scripts resolve relative to the
   server's working directory).
5. Verify with a local MariaDB (or the docker stack) by running `db-init` twice: first applies,
   second reports nothing to do.

## The DA (data access) pattern

`TSOClient/FSO.Server.Database/DA/` — one folder per entity. Each has:

- `IXxx` interface + `SqlXxx` Dapper implementation (constructor takes the context).
- Registered/reached through `IDAFactory` → `MySqlDAFactory` / `SqliteDAFactory`, consumed as
  `using (var da = DAFactory.Get()) { da.Xxx.Method(...); }`.
- Model classes (`DbXxx`) map columns by property name.

**Both MySQL and SQLite must work.** SQLite is a supported backend (`SqliteContext`,
`SqliteCompat/`): avoid MySQL-only syntax in Dapper queries (e.g. `INSERT ... ON DUPLICATE KEY`,
`LAST_INSERT_ID()` quirks); check how `SqliteCompat/` translates existing queries when in doubt, and
mirror an existing DA that does something similar.

## Exposing new data to the game

Persisted data reaches clients one of three ways — pick the one used by the nearest analogous
feature:
- **DataService object graph** (`FSO.Common.DataService`, in `FSO.Server.DataService/`): live-synced
  avatar/lot/city state, addressed by ID, requested via `DataServiceWrapperPDU`.
- **Voltron PDU** (`FSO.Server.Protocol/Voltron/Packets/`): explicit request/response or push
  messages between client and city server.
- **UserApi** (`FSO.Server.Api.Core`): plain HTTP for out-of-game surfaces (registration, admin).

## Ops notes

- Production DB is MariaDB 11 via `docker/docker-compose.yml`; connection string in the mounted
  `config.json`.
- Scheduled maintenance (prune, bonuses, neighborhood ticks) is the `tasks` service configured in
  `config.json`, implemented in `FSO.Server/Servers/Tasks/`.
- Manual data surgery conventions (admin flags, etc.): `Documentation/Database Manipulation.md`.
