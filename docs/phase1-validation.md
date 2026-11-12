# Fase 1 — Estabilización y pruebas

Verificación realizada el 10 de noviembre de 2026. Se implementó únicamente la Fase 1; no se inició persistencia, administración ni despliegue.

## Inspección y alcance preservado

Se revisaron README, ROADMAP, dominio, contratos, coordinador de partidas, Hub, arranque del servidor y cliente. Git estaba limpio antes de editar. No existían proyectos de pruebas ni una solución en la raíz.

- `Game.Domain`: .NET 10 sin dependencias de red o persistencia; motor simultáneo, energía, reserva, bloqueos, críticos y especial. No se modificó el dominio ni sus reglas.
- `Game.Server`: ASP.NET Core, SignalR, JWT/Auth0, OpenAPI y Scalar; referencia al dominio. Se mantienen salas públicas/privadas, emparejamiento, nicknames, validación, idempotencia, snapshots privados, abandono e inactividad en memoria.
- `web-client`: React 19, TypeScript, Vite, SDK Auth0 y SignalR. Se conservan estilos, componentes visuales y mecanismos de autenticación/conexión.
- Continúan activados nullable y warnings como errores en `Directory.Build.props`.

## Archivos creados

- `ProyectoServer.slnx`: permite ejecutar ambas suites con `dotnet test` desde la raíz.
- `tests/Game.Domain.Tests/Game.Domain.Tests.csproj`.
- `tests/Game.Domain.Tests/RoundResolverTests.cs`.
- `tests/Game.Server.Tests/Game.Server.Tests.csproj`.
- `tests/Game.Server.Tests/MatchRoundServiceTests.cs`.
- `tests/Game.Server.Tests/MessageValidationTests.cs`.
- `docs/phase1-validation.md`: este informe.

Las suites usan Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3 y runner Visual Studio 3.1.4, ejecutados con SDK .NET 10.0.403.

## Archivos modificados y errores corregidos

- `docs/asyncapi.yaml`: cuatro summaries con `Planned:` entre comillas; `winnerId` requerido y nullable en snapshot, con ejemplo actualizado. Se documentó también `abandonment`, que el servidor ya emitía como motivo de cierre.
- `src/Game.Server/Rooms/MatchRoundService.cs`: snapshot incluye `winnerId`, conservando ganador o empate tras recuperar una partida terminada.
- `web-client/src/App.tsx`: `winnerId` obligatorio en el tipo del snapshot; estado visible de conexión derivado de autenticación y limpieza del error de partida al recibir el snapshot. Se eliminaron las actualizaciones síncronas de estado señaladas por ESLint sin deshabilitar reglas ni modificar CSS/JSX.
- `src/Game.Server/Contracts/V1/SubmitRoundPlanMessageValidator.cs`: una prueba demostró que `Enum.TryParse` aceptaba `"0"` como `Attack`, fuera del enum textual publicado. Ahora exige el nombre exacto de la acción; se mantienen los nombres y valores válidos del contrato.
- `README.md`: instrucciones para ejecutar las pruebas y enlace al informe.

## Pruebas incorporadas

**Dominio: 25 casos ejecutados**, con implementación determinista de `ICriticalHitSource`:

1. Estado inicial de ambos jugadores, secuencia y fecha.
2. Gasto exacto de energía: rechazo por defecto y por exceso.
3. Recuperación entre rondas y avance de secuencia.
4. Reserva acumulada y consumo sin arrastre duplicado.
5. Quinto bloqueo permitido y sexto rechazado.
6. Bloqueo completo de ataque normal, incluido crítico.
7. Daño triple y fronteras 0/999/1000/9999; recorrido de los 10.000 valores verifica exactamente 1.000 críticos.
8. Especial rechazado antes de ronda 5 y permitido en ronda 5.
9. Especial fijo de 20, sin crítico, no reutilizable ni duplicable en un mismo plan.
10. Bloqueo prioriza especial sobre ataque normal.
11. Victoria por KO y salud limitada a cero.
12. Empate por KO simultáneo.
13. Cierre en ronda 10 con victoria de cualquiera o empate según salud; rechazo de nuevas rondas tras finalizar.
14. Rechazo de acciones desconocidas, jugador incorrecto y lista ausente.

**Servidor: 27 casos ejecutados**, con un `IHubContext<GameHub>` de prueba que captura destinatario, evento y payload real serializado:

- Estructura del mensaje: tipo, versión, identificadores, secuencia negativa, acciones ausentes/vacías, límite 19, acciones desconocidas, mayúsculas y cadenas numéricas.
- Secuencias futuras/obsoletas, plan duplicado por jugador y requisitos de nickname y energía.
- Reintentos idénticos antes/después de resolver: reproducción exacta del acuse/resultado, sin daño ni secuencia duplicados, incluso a otra conexión.
- Reutilización de requestId con contenido o secuencia diferente; separación de claves por participante.
- Rechazo de lecturas, planes, nicknames y abandono de un no participante.
- Snapshots desde ambas perspectivas: solo el estado propio es privado, sin energía, recursos ni planes del rival.
- Snapshots completados con ganador o empate; abandono e inactividad con empate o victoria del jugador activo; cierre no repetido.

## Resultados exactos

| Verificación | Resultado |
| --- | --- |
| `dotnet build src/Game.Server/Game.Server.csproj --configuration Release` | Exit 0; 0 advertencias, 0 errores. |
| `dotnet test --configuration Release` | Exit 0; dominio 25/25 y servidor 27/27; 52 aprobadas, 0 fallidas, 0 omitidas. |
| `npm.cmd ci` en `web-client` | Exit 0; 187 paquetes instalados. |
| `npm.cmd run lint` | Exit 0; sin errores ni advertencias de ESLint. |
| `npm.cmd run build` | Exit 0; TypeScript y Vite correctos; 45 módulos transformados. |
| YAML con parser `yaml` 2.8.1 | Sin errores sintácticos ni claves duplicadas; AsyncAPI 3.0.0; 99 referencias internas resueltas; winnerId requerido en snapshot. |
| `git diff --check` | Exit 0. |

La comprobación YAML usó un parser temporal, sin agregar dependencias al cliente. Comprueba sintaxis y referencias internas; no equivale a una validación semántica completa contra el metaschema AsyncAPI.

## Limitaciones y advertencias pendientes

- No se ejecutaron flujos de navegador con dos sesiones Auth0 reales, renovación del token ni interrupciones reales de WebSocket. La autorización probada corresponde a pertenencia en el componente, no a una integración HTTP/JWT completa.
- Vite advierte por un chunk mayor de 500 kB: JavaScript 530,38 kB, gzip 153,50 kB. No impide el build; optimización de carga queda para una fase posterior.
- `npm ci` informó que bloqueó el postinstall de `browser-tabs-lock@1.3.0` por su política de scripts. No se cambió esa política. Lint/build pasan; el comportamiento de autenticación entre pestañas requiere comprobación en navegador.
- El lanzador `npm.ps1` está bloqueado por la política de ejecución de PowerShell; `npm.cmd` permitió completar los tres comandos sin cambiar políticas del equipo.
- `dotnet new` intentó escribir inicialmente en una caché fuera del workspace y fue rechazado. La solución se generó con una caché temporal dentro del repositorio y los proyectos de pruebas se crearon directamente; la caché se retiró al finalizar.

## Riesgos para la fase siguiente

- Estado, secuencias e idempotencia siguen en memoria y por instancia; PostgreSQL deberá conservarlos atómicamente. No se implementó persistencia.
- La reconexión necesita una prueba real de integración: actualmente el snapshot no informa si ya existe un plan pendiente y el cliente restablece `planPending` a false al recibirlo; el servidor sigue rechazando un segundo plan. Este comportamiento preexistente debe considerarse al diseñar recuperación.
- El snapshot recupera ganador/empate, pero no contiene el motivo de cierre ni los efectos de la última ronda; no reconstruye esos detalles tras perderlos en el cliente.
- No se realizaron commits, push, despliegues ni cambios en archivos de entorno o secretos.
