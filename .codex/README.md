# D3Parking Codex development modes

The `SessionStart` hook selects the host-specific setup automatically:

- Linux runs `hooks/session-start.sh`.
- Windows runs `hooks/session-start.ps1` through `commandWindows`.

Both modes validate .NET 10 and restore `dotnet-ef` and NuGet packages. The SQL backend is selected independently:

- `Auto` — the default. Windows prefers an external connection, then LocalDB, then Docker. Linux prefers an external connection, then Docker or Podman.
- `LocalDb` — Windows only; uses `(localdb)\MSSQLLocalDB` and does not need Docker.
- `External` — uses `D3PARKING_SQL_CONNECTION`; suitable for a company SQL Server or a locally installed instance.
- `Containers` — runs SQL Server 2022 and smtp4dev through Docker or Podman.

Run database-dependent commands through the wrapper for the current OS:

```bash
.codex/run-with-dev-env.sh dotnet test
```

```powershell
.codex/run-with-dev-env.ps1 dotnet test
```

## Without Docker on Windows

Install SQL Server Express LocalDB (usually through Visual Studio Installer), then run:

```powershell
.codex/run-with-dev-env.ps1 -Backend LocalDb dotnet test
```

`Auto` selects LocalDB automatically when `sqllocaldb.exe` is available.

## External SQL Server without Docker

Set the connection string for the current shell. It is only copied into ignored local environment files and is not committed:

```powershell
$env:D3PARKING_SQL_CONNECTION = 'Server=sql.company.example;Database=D3Parking;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
.codex/run-with-dev-env.ps1 -Backend External dotnet test
```

```bash
export D3PARKING_SQL_CONNECTION='Server=sql.company.example;Database=D3Parking;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True'
.codex/run-with-dev-env.sh --backend external dotnet test
```

In `LocalDb` and `External` modes smtp4dev is optional. The application keeps working if no SMTP server is configured; background email delivery is the only unavailable feature.

The wrappers export the application and EF design-time connection variables for the child command. Container mode additionally configures smtp4dev on SMTP port `2525` and web UI port `5000`.
