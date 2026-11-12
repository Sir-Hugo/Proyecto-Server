# Phase 5A local performance runner

Run from the repository root:

```powershell
.\scripts\performance\run.ps1
```

The script selects a free loopback port, creates a temporary random PostgreSQL password outside the repository, starts an isolated PostgreSQL 17 Compose project, applies the real migrations, runs the .NET server in Release through `Microsoft.AspNetCore.TestHost`, writes individual and aggregate measurements, and removes the container, volume and temporary environment file in `finally`.

The synthetic JWT issuer and signing key exist only inside the runner process. Production credentials, Auth0 and Azure are never contacted. Normal security limits remain enabled. SignalR uses Long Polling because TestServer does not expose a network WebSocket listener; this makes the run reproducible but means the numbers must not be extrapolated to Azure.
