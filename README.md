# Proyecto Server

Servidor autoritativo para un juego por turnos de dos jugadores. Integra una API ASP.NET Core, SignalR, PostgreSQL y un cliente React servido desde el mismo contenedor. La aplicación usa Auth0, conserva partidas y sesiones, permite historial y replay privados, y expone administración auditada mediante el permiso `admin:game`.

**Servidor público:** https://gameproj-dev-app.salmonmoss-7dc2e6dd.northcentralus.azurecontainerapps.io/

## Tecnologías

- .NET 10, ASP.NET Core, SignalR y Npgsql.
- PostgreSQL 17 con migraciones SQL 001–004.
- React 19, TypeScript y Vite.
- Auth0 con JWT y permisos de API.
- Docker, Azure Container Apps, ACR, Key Vault y Bicep.
- GitHub Actions, CodeQL, gitleaks, OpenTelemetry y Application Insights.

## Componentes

| Ruta | Contenido |
| --- | --- |
| `src/Game.Domain` | Reglas autoritativas y resolución de rondas. |
| `src/Game.Infrastructure` | Persistencia PostgreSQL, migraciones, historial y auditoría. |
| `src/Game.Server` | REST, SignalR, Auth0, seguridad, salud y observabilidad. |
| `web-client` | Cliente React para Auth0, salas, partidas y recuperación. |
| `tools/Game.Database` | Migrador explícito; el servidor no migra al arrancar. |
| `tests` | Pruebas de dominio, servidor, PostgreSQL, REST y SignalR. |
| `scripts/bot` | Bot local autónomo que juega una partida completa mediante REST y SignalR. |
| `scripts/performance` | Clientes automatizados y mediciones locales de carga. |
| `infra` | Bicep del ambiente y deployments aislados. |
| `.github/workflows` | CI y CD manual protegido. |

## Ejecución local con Docker

Requisitos: Docker Desktop, PowerShell y una aplicación SPA/API de desarrollo en Auth0. El dominio, audience y Client ID son identificadores públicos; no agregue Client Secrets al repositorio.

1. Cree el archivo local ignorado y defina una contraseña solo para desarrollo:

   ```powershell
   Copy-Item env/postgres.env.example env/postgres.env
   # Edite env/postgres.env y complete POSTGRES_PASSWORD.
   ```

2. Configure los valores públicos de Auth0 en la sesión:

   ```powershell
   $env:AUTH0_AUTHORITY = 'https://TENANT.us.auth0.com/'
   $env:AUTH0_AUDIENCE = 'https://game-api'
   $env:AUTH0_CLIENT_ID = 'SPA_PUBLIC_CLIENT_ID'
   $env:PUBLIC_ORIGIN = 'http://localhost:8080'
   ```

3. Construya, inicie PostgreSQL, aplique las migraciones una vez y levante la aplicación:

   ```powershell
   docker compose --env-file env/postgres.env --profile app build server
   docker compose --env-file env/postgres.env up -d --wait postgres
   docker compose --env-file env/postgres.env --profile app run --rm --no-deps --entrypoint dotnet server /app/database/Game.Database.dll migrate
   docker compose --env-file env/postgres.env --profile app up -d --wait server
   ```

4. Abra `http://localhost:8080`. Auth0 debe permitir esa URL como callback, logout y web origin para la aplicación local.

Para detener el ambiente:

```powershell
docker compose --env-file env/postgres.env --profile app down
```

Agregue `--volumes` solo si desea eliminar también los datos locales.

## Desarrollo del cliente

Para ejecutar Vite separado del servidor, copie `web-client/.env.example` a `web-client/.env.local`, complete los identificadores públicos y ejecute:

```powershell
Set-Location web-client
npm ci
npm run dev
```

## Bot de prueba

El bot local inicia dos jugadores autónomos contra el host real en memoria y un PostgreSQL 17 aislado. Usa JWT efímeros y no acepta una URL externa, por lo que no puede enviar tráfico accidental a producción:

```powershell
.\scripts\bot\run.ps1
```

La estrategia gasta toda la energía en ataques normales. El flujo crea y une la sala, marca readiness, configura nicknames, envía planes legales y verifica el historial al terminar. Consulte [`scripts/bot/README.md`](scripts/bot/README.md) para sus límites.

## Pruebas y validación

La suite PostgreSQL necesita Docker:

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

El CI también construye la imagen, compila Bicep, valida workflows, revisa dependencias, ejecuta CodeQL y gitleaks, y valida ambos contratos.

## Contratos y operaciones

- OpenAPI 3.1: `/docs/` y `/openapi/v1.json` en el servidor en ejecución.
- AsyncAPI 3.0: [`docs/asyncapi.yaml`](docs/asyncapi.yaml).
- Liveness: `/health/live`.
- Readiness PostgreSQL: `/health/ready`.
- SignalR: `/hubs/game` mediante HTTPS/WSS en Azure.

## Rendimiento

Ejecute el escenario local reproducible con PostgreSQL aislado:

```powershell
.\scripts\performance\run.ps1
```

El método, resultados y límites están en [`docs/phase5a-performance-validation.md`](docs/phase5a-performance-validation.md). La medición local no demuestra latencia extremo a extremo en Azure, consumo por partida ni el punto de ruptura de una réplica.

## Infraestructura, seguridad y evidencia

- Infraestructura y deployments aislados: [`infra/`](infra/).
- CI: [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- CD manual pendiente de configuración externa: [`.github/workflows/cd.yml`](.github/workflows/cd.yml).
- Modelo de amenazas: [`docs/threat-model.md`](docs/threat-model.md).
- Validación de seguridad: [`docs/phase4a-security-validation.md`](docs/phase4a-security-validation.md).
- Observabilidad y alertas: [`docs/phase7b-observability.md`](docs/phase7b-observability.md).
- Costos: [`docs/azure-costs.md`](docs/azure-costs.md).
- Estado de entrega y matriz de cumplimiento: [`docs/release-readiness.md`](docs/release-readiness.md).
- Evidencias visuales revisadas: [`docs/evidence/`](docs/evidence/).

## Limitaciones conocidas

- Azure mantiene una sola réplica; los límites y registros de conexiones en memoria no están coordinados entre réplicas.
- El workflow de CD está preparado, pero no se ha ejecutado con GitHub OIDC y Environments.
- Canary, división de tráfico y drenado de conexiones no fueron demostrados.
- La prueba local llegó a 20 partidas concurrentes, pero no localizó el punto de ruptura ni midió CPU/memoria por partida.
- No existe comparación medida antes/después de una optimización.
- El bot es una demostración local bot contra bot con Long Polling; no es una IA competitiva ni prueba WebSocket contra Azure.

La rama de entrega aún no debe etiquetarse como `v1.0.0` hasta integrar y validar por CI el trabajo final del frontend.
