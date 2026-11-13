# Proyecto Server

Servidor autoritativo para un juego de combate por turnos entre dos jugadores. Incluye API ASP.NET Core, SignalR, PostgreSQL, cliente React, autenticación Auth0, historial privado, replay sanitizado y administración auditada.

**Aplicación pública:** https://gameproj-dev-app.salmonmoss-7dc2e6dd.northcentralus.azurecontainerapps.io/

## Componentes

| Ruta | Contenido |
| --- | --- |
| `src/Game.Domain` | Reglas y resolución autoritativa de rondas. |
| `src/Game.Infrastructure` | PostgreSQL, migraciones, sesiones, historial y auditoría. |
| `src/Game.Server` | REST, SignalR, Auth0, seguridad, salud y OpenTelemetry. |
| `web-client` | Cliente React y TypeScript. |
| `tools/Game.Database` | Migrador explícito del esquema. |
| `tests` | Pruebas de dominio, servidor, REST, SignalR y PostgreSQL. |
| `scripts/bot` | Dos clientes autónomos para una partida local completa. |
| `scripts/performance` | Escenarios locales reproducibles de carga. |
| `infra` | Infraestructura y deployments aislados en Bicep. |

## Requisitos

- .NET 10 SDK.
- Node.js 24 y npm.
- Docker Desktop con Docker Compose.
- PowerShell.
- Una aplicación SPA/API de desarrollo en Auth0 para usar la interfaz local.

## Ejecución local con Docker

1. Cree el archivo local de PostgreSQL y defina una contraseña de desarrollo:

   ```powershell
   Copy-Item env/postgres.env.example env/postgres.env
   # Edite env/postgres.env y complete POSTGRES_PASSWORD.
   ```

2. Configure los identificadores públicos de Auth0:

   ```powershell
   $env:AUTH0_AUTHORITY = 'https://TENANT.us.auth0.com/'
   $env:AUTH0_AUDIENCE = 'https://game-api'
   $env:AUTH0_CLIENT_ID = 'SPA_PUBLIC_CLIENT_ID'
   $env:PUBLIC_ORIGIN = 'http://localhost:8080'
   ```

3. Construya la imagen, inicie PostgreSQL, aplique las migraciones y levante el servidor:

   ```powershell
   docker compose --env-file env/postgres.env --profile app build server
   docker compose --env-file env/postgres.env up -d --wait postgres
   docker compose --env-file env/postgres.env --profile app run --rm --no-deps --entrypoint dotnet server /app/database/Game.Database.dll migrate
   docker compose --env-file env/postgres.env --profile app up -d --wait server
   ```

4. Abra `http://localhost:8080`. Esa URL debe estar autorizada como callback, logout y web origin en la aplicación Auth0 local.

Para detener el ambiente:

```powershell
docker compose --env-file env/postgres.env --profile app down
```

Agregue `--volumes` únicamente si también desea eliminar los datos locales.

## Desarrollo del cliente

```powershell
Copy-Item web-client/.env.example web-client/.env.local
Set-Location web-client
npm ci
npm run dev
```

Complete `web-client/.env.local` con los identificadores públicos de Auth0. No agregue Client Secrets al cliente ni al repositorio.

## Pruebas

La suite de PostgreSQL utiliza Docker:

```powershell
dotnet restore ProyectoServer.slnx
dotnet build ProyectoServer.slnx --configuration Release --no-restore
$env:GAME_POSTGRES_TESTS = '1'
dotnet test ProyectoServer.slnx --configuration Release --no-build

Set-Location web-client
npm ci
npm run lint
npm test
npm run build
```

## Bot y carga

Ejecute una partida local autónoma entre dos bots:

```powershell
.\scripts\bot\run.ps1
```

El bot no acepta una URL externa y no contacta producción. Sus límites están en [`scripts/bot/README.md`](scripts/bot/README.md).

Ejecute el escenario local de rendimiento:

```powershell
.\scripts\performance\run.ps1
```

El resultado se genera en `scripts/performance/results/phase5a-results.json` y no se versiona. El método y sus límites están en [`scripts/performance/README.md`](scripts/performance/README.md).

## Contratos y operación

- OpenAPI 3.1: `/docs/` y `/openapi/v1.json` en el servidor en ejecución.
- AsyncAPI 3.0: [`docs/asyncapi.yaml`](docs/asyncapi.yaml).
- SignalR: `/hubs/game`.
- Liveness: `/health/live`.
- Readiness PostgreSQL: `/health/ready`.
- Modelo de amenazas: [`docs/threat-model.md`](docs/threat-model.md).
- Evidencias saneadas: [`docs/evidence/`](docs/evidence/).
- Infraestructura: [`infra/`](infra/).
- CI y CD manual: [`.github/workflows/`](.github/workflows/).

## Documentación técnica

La documentación técnica completa se entrega **por separado**, fuera de este repositorio, como `Documentacion_Proyecto3.pdf`. La versión editable se entrega como `Documentacion_Proyecto3.md`. En el árbol local de preparación se encuentran en `../Entrega-Proyecto3/`.

## Limitaciones principales

- Azure ejecuta una sola réplica; no se demostró coordinación distribuida.
- El workflow de CD está preparado, pero no se ejecutó desde GitHub Actions.
- Canary, drenado de conexiones y restauración desde backup no fueron ensayados.
- La prueba local llegó a 20 partidas concurrentes; no determinó el punto de ruptura ni el consumo aislado por partida.
- El bot utiliza TestServer y Long Polling, no Auth0 ni WebSocket contra Azure.
