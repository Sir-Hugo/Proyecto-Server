# Fase 4A — Auditoría y cierre local de seguridad

Fecha de validación: 12 de noviembre de 2026. Esta fase es exclusivamente local: no modifica Azure, Auth0, PostgreSQL de producción ni imágenes publicadas. Las comprobaciones con JWT de prueba usan el middleware real de ASP.NET Core y una clave efímera local; no sustituyen las pruebas manuales ya realizadas con Auth0 en Azure.

## Matriz de cumplimiento

| Control | Estado | Implementación y evidencia |
|---|---|---|
| Firma, issuer, audience y expiración JWT | Implementado y probado | `AddJwtBearer` usa Authority/Audience y HTTPS metadata. La integración rechaza firma, issuer, audience y expiración incorrectos con 401. Auth0 real fue comprobado manualmente en Azure. |
| Permiso administrativo `admin:game` | Implementado y probado | La política exige la claim `permissions` exacta. Usuario común y claim `role=admin` falsa reciben 403; el permiso Auth0 real obtuvo 200 en Azure. |
| Autorización de salas, partidas, historial y replay | Implementado y probado | REST y Hub requieren autenticación; pertenencia se valida en servidor. Detalle y replay ajenos responden 404. |
| Usuarios suspendidos | Implementado y probado | Middleware HTTP y filtro SignalR consultan la suspensión persistente. Hay regresión HTTP/Hub y auditoría append-only. |
| Bypasses de producción | Implementado y probado | Producción exige Authority, Audience y Client ID; no existe esquema de autenticación de prueba en el servidor. |
| Rate limiting HTTP | Implementado y probado | Ventana fija: 120 solicitudes/min por `sub`, 30/min por IP directa para anónimos; exceso devuelve 429, `Retry-After` y código estable. |
| Rate limiting SignalR | Implementado y probado | 240 solicitudes de transporte/min por usuario, 60 invocaciones/10 s por conexión, 120 invocaciones/10 s por usuario y máximo de 5 conexiones simultáneas. El contador de usuario sobrevive reconexiones y expira por inactividad. |
| Tamaño y validación de entradas | Implementado y probado | Cuerpo HTTP máximo 64 KiB, mensaje SignalR máximo 8 KiB, planes máximo 19 acciones, nickname y razones administrativas acotados. |
| Manejo de errores | Implementado y probado | REST usa Problem Details/códigos estables y no expone conexión ni subjects. SignalR usa códigos breves de HubException documentados. |
| CORS y origen WebSocket | Implementado y probado | Lista explícita `Cors:AllowedOrigins`, credenciales y orígenes WebSocket restringidos. |
| Token SignalR en query string | Parcialmente implementado | Solo se acepta `access_token` bajo `/hubs/game`, como requiere el cliente WebSocket de navegador. No existe ticket de un solo uso; debe evitarse registrar query strings en proxies. |
| Proxies y encabezados reenviados | Implementado y probado | La aplicación no confía en headers reenviados. Container Apps impone HTTPS/WSS con `allowInsecure=false`; producción emite HSTS sin redirigir el HTTP interno posterior a la terminación TLS. `X-Forwarded-For` no se acepta como identidad ni clave de límite. |
| Encabezados HTTP defensivos | Implementado y probado | HSTS en producción; `nosniff`, `DENY`, `no-referrer` y Permissions-Policy en todas las respuestas. CSP queda pendiente por compatibilidad con el flujo Auth0. |
| Idempotencia y duplicados | Implementado y probado | `requestId`, contenido asociado y restricciones PostgreSQL evitan doble aplicación; reintentos idénticos recuperan el resultado. |
| Concurrencia y aislamiento | Implementado y probado | Bloqueos por fila, transacciones y secuencia esperada serializan cada partida; pruebas demuestran que una partida bloqueada no detiene otra. |
| Consultas parametrizadas y transacciones | Implementado y probado | Npgsql usa parámetros; creación, rondas, terminación y auditoría se confirman transaccionalmente. |
| Privacidad por jugador | Implementado y probado | Snapshots, resultados y replay omiten planes, tiradas internas, request IDs y subjects; las vistas se proyectan por participante. |
| PostgreSQL privado y TLS | Implementado y probado | Bicep deshabilita acceso público y usa subred delegada; el despliegue Azure fue verificado `Ready` y privado. |
| Key Vault e identidad administrada | Implementado y probado | Container App y Migration Job referencian un secreto versionado mediante identidad administrada y roles acotados. |
| HTTPS/WSS | Implementado y probado | Ingress no permite HTTP inseguro; HSTS y forwarded proto mantienen el esquema externo. |
| Secretos en repositorio, logs e imagen | Implementado y probado | Configuración pública solo contiene Authority/Audience/Client ID; logs PostgreSQL registran operación y SQLSTATE. CI usa gitleaks. |
| Cadena de suministro | Parcialmente implementado | CI ejecuta CodeQL, gitleaks, `dotnet list package --vulnerable` y `npm audit`; falta un escaneo explícito de la imagen final y una política que bloquee CVE críticos. La consulta local no encontró paquetes NuGet vulnerables; npm reportó 10 avisos moderados transitivos de herramientas ESLint/minimatch y ninguna vulnerabilidad alta o crítica. |
| Rate limiting con varias réplicas | No aplicable al despliegue actual | El ambiente tiene exactamente una réplica. Los contadores son locales; antes de escalar se necesita almacenamiento/coordinación distribuida. |
| Métricas de seguridad y alertas | Parcialmente implementado | Application Insights y Log Analytics existen, pero faltan métricas propias, alertas de 401/403/429 y prueba documentada de alertas. |
| Prueba de carga y punto de quiebre | Ausente | Debe medirse p95, errores, CPU/memoria por partida y capacidad antes de declarar el requisito de rendimiento. |

## Correcciones realizadas

- Rate limiting HTTP por usuario o IP directa, con 429 y `Retry-After`.
- Límites separados del transporte, de cada conexión y del usuario SignalR, más máximo simultáneo; reconectar no reinicia el contador por `sub`.
- Eliminación de la confianza en `X-Forwarded-For` y `X-Forwarded-Proto`; HTTPS/WSS se impone en el ingress y HSTS se emite sin redirección interna.
- Límite de 64 KiB para cuerpos HTTP y respuesta 413 estable.
- HSTS en producción y encabezados defensivos sin cambiar el flujo Auth0.
- Pruebas de JWT inválido/vencido, rate limiting, límites de conexión, tamaño y encabezados.

## Valores y rendimiento esperado

Los límites permiten 120 solicitudes REST por minuto por usuario, 240 solicitudes de transporte SignalR por minuto, 60 invocaciones del Hub cada 10 segundos por conexión, 120 por usuario y cinco conexiones concurrentes. Son muy superiores al ritmo normal de un juego por turnos. El costo adicional es una consulta a contadores en memoria por solicitud/invocación y una búsqueda pequeña al conectar; no hay acceso adicional a PostgreSQL. Los contadores viven en cada réplica y no deben presentarse como límite global si se habilita escalado horizontal.

## Evidencia reproducible

Resultado final:

- Build .NET Release: correcto, 0 errores y 0 advertencias.
- Pruebas .NET: 152/152 aprobadas; 99 usan PostgreSQL 17 real en Testcontainers.
- Frontend: 3/3 pruebas aprobadas.
- ESLint y build Vite: correctos. Vite advierte que el bundle principal supera 500 KiB; corresponde a optimización de rendimiento, no a un fallo de seguridad.
- NuGet: ninguna dependencia vulnerable reportada.
- npm: ninguna vulnerabilidad alta o crítica; 10 moderadas transitivas del toolchain ESLint/minimatch.
- `git diff --check`: correcto.

Los resultados TRX se generan con:

```powershell
$env:GAME_POSTGRES_TESTS='1'
dotnet test ProyectoServer.slnx --configuration Release --no-restore --logger trx --results-directory TestResults/phase4a
```

La validación complementaria es:

```powershell
dotnet build ProyectoServer.slnx --configuration Release
npm test
npm run lint
npm run build
git diff --check
```

`TestResults/` permanece fuera del producto y no contiene tokens ni secretos. La siguiente fase debe medir carga, incorporar escaneo de la imagen y decidir CSP/alertas antes de publicar otra imagen.

La auditoría de dependencias no aplicó `npm audit fix --force`: la corrección propuesta bajaría ESLint a una versión mayor distinta y puede romper la configuración. Los avisos actuales pertenecen al toolchain de desarrollo, no al bundle de ejecución, y deben resolverse mediante actualizaciones compatibles verificadas en una fase separada.
