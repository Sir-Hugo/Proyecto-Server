# Fase 5A — Validación local de rendimiento y concurrencia

Fecha de ejecución: 12 de noviembre de 2026. Commit evaluado: `9a2883e6c1e5518c19ff7760f258ce367632adce`.

## Resultado ejecutivo

La ejecución válida produjo 375 muestras individuales, sin errores, HTTP 429 ni timeouts. El ciclo de ronda que exige la rúbrica —desde el envío concurrente de ambos planes hasta que los dos clientes reciben `round.resolved`, después de la confirmación en PostgreSQL— obtuvo p95 de 52.05 ms con una partida, 64.47 ms con 10 partidas y 101.96 ms con 20 partidas. Los tres escenarios quedaron por debajo de 200 ms.

El resultado respalda el cumplimiento local del criterio, pero no prueba el rendimiento de Azure: el servidor se ejecutó en Release dentro de `Microsoft.AspNetCore.TestHost`, SignalR usó Long Polling y PostgreSQL estuvo en Docker en el mismo equipo. La prueba fue deliberadamente breve y se detuvo en 20 partidas; no se determinó el punto de ruptura.

## Requisito y arquitectura auditada

El enunciado exige p95 menor a 200 ms entre la llegada de una jugada y la recepción del nuevo estado por todos los jugadores, tasa de error menor a 1 %, ninguna jugada perdida o duplicada y un informe de capacidad con CPU, memoria y punto de ruptura. No se aplicó el umbral de 200 ms como requisito inventado a todas las rutas HTTP; estas se informan para diagnóstico.

| Operación | Transporte | Persistencia relevante |
| --- | --- | --- |
| Crear, unir y consultar sala | HTTP | PostgreSQL (`rooms`, `room_members`) |
| Marcar listo e iniciar partida | HTTP + aviso SignalR | Transacción de partida, participantes y evento inicial |
| Enviar plan y resolver ronda | SignalR | Comandos idempotentes, planes pendientes, estado, evento y outbox |
| Recuperar partida | SignalR | Lectura del estado y secuencia confirmados |
| Historial, detalle y replay | HTTP | Consultas PostgreSQL; replay sanitizado |

El servidor conserva los límites configurados: REST 120 solicitudes/minuto por usuario, transporte SignalR 240/minuto por usuario, 60 invocaciones/10 s por conexión, 120 invocaciones/10 s por usuario y cinco conexiones simultáneas por usuario. Cada jugador sintético tuvo un `sub` distinto y la carga normal se mantuvo por debajo de esos límites.

## Entorno y método

- Windows 11 Pro 10.0.26200, x64; 12 procesadores lógicos; 24,280 MB de RAM física.
- .NET SDK 10.0.403, runtime 10.0.14; Docker 29.8.2; Compose 5.6.0; Node 24.16.0.
- PostgreSQL 17.11 `postgres:17.11-bookworm`, creado con Docker Compose en un proyecto y volumen exclusivos.
- Backend compilado en Release y ejecutado con TestServer. JWT HMAC, issuer, audiencia, usuarios y contraseña de PostgreSQL fueron sintéticos y efímeros.
- Se aplicaron las migraciones reales 001–004 antes de la carga. El calentamiento de una partida se excluyó de las métricas.
- Los percentiles se calcularon sobre muestras individuales ordenadas, con interpolación lineal `(n - 1) × p`.
- La métrica `signalr.round.all-clients` empieza antes de enviar ambos planes y termina después de recibir y validar el mismo número de secuencia en ambos clientes. Por ello incluye validación, bloqueo/transacción PostgreSQL, resolución, persistencia y entrega a los dos jugadores.
- CPU y memoria corresponden al proceso conjunto del ejecutor/TestServer. PostgreSQL se ejecutó en otro proceso Docker y no está incluido en esa cifra; esta es una limitación de la medición de recursos.

El script genera una contraseña criptográficamente aleatoria en memoria, la escribe únicamente en un archivo temporal fuera del repositorio, y elimina archivo, contenedor, red y volumen en `finally`. No se usaron credenciales reales ni se contactó Azure o Auth0.

## Escenarios ejecutados

| Escenario | Usuarios | Partidas concurrentes | Muestras | Duración | Throughput observado |
| --- | ---: | ---: | ---: | ---: | ---: |
| A, línea base | 2 | 1 | 10 | 0.371 s | 26.96 op/s |
| B, moderado | 20 | 10 | 90 | 0.520 s | 173.01 op/s |
| C, elevado controlado | 40 | 20 | 160 | 0.758 s | 211.12 op/s |
| D, persistencia | 25 identidades | 0 | 75 | 0.347 s | 215.86 op/s |
| E, reconexión | 10 | 5 | 40 | 0.334 s | 119.62 op/s |

El throughput es el número de operaciones instrumentadas dividido entre el tiempo de pared del escenario. Como varias operaciones corren en paralelo, no debe confundirse con el inverso de una latencia media.

## Latencias

### Criterio académico: resolución y entrega de ronda

| Escenario | n | p50 | p95 | p99 | Máximo | Errores | Evaluación |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| A, 1 partida | 5 | 44.62 ms | 52.05 ms | 53.46 ms | 53.81 ms | 0 | Cumple; muestra pequeña |
| B, 10 partidas | 30 | 61.52 ms | 64.47 ms | 66.36 ms | 67.06 ms | 0 | Cumple |
| C, 20 partidas | 40 | 93.97 ms | 101.96 ms | 105.23 ms | 106.99 ms | 0 | Cumple |

La tasa de error global fue 0/375 (0 %), por debajo de 1 %. Cada respuesta de ronda verificó que ambos clientes recibieran exactamente la secuencia esperada. La comprobación final encontró cero secuencias duplicadas en `match_events`, cero comandos sin resultado y cero planes pendientes. Estas evidencias cubren la ejecución observada; no constituyen una prueba formal para toda carga posible.

### HTTP y establecimiento SignalR

| Operación | A p50 / p95 | B p50 / p95 | C p50 / p95 |
| --- | ---: | ---: | ---: |
| Crear sala | 12.55 / 12.55 ms | 34.14 / 41.51 ms | 25.56 / 34.31 ms |
| Unir sala | 10.52 / 10.52 ms | 20.66 / 27.59 ms | 19.20 / 21.46 ms |
| Consultar sala | 6.49 / 6.49 ms | 14.46 / 21.43 ms | 13.10 / 40.64 ms |
| Iniciar partida | 32.47 / 32.47 ms | 49.13 / 75.56 ms | 103.17 / 161.42 ms |
| Conectar dos clientes SignalR | 6.58 / 6.58 ms | 32.89 / 47.79 ms | 37.34 / 72.17 ms |

`http.match.start` fue la operación más cercana al umbral bajo carga alta, con p95 161.42 ms y máximo 170.73 ms. Es una transacción de creación con varias escrituras y el aviso posterior; merece observación en pruebas futuras aunque el enunciado no le asigna explícitamente el umbral de ronda.

### Persistencia y reconexión

| Operación | n | p50 | p95 | p99 | Máximo |
| --- | ---: | ---: | ---: | ---: | ---: |
| Historial paginado | 25 | 3.88 ms | 5.91 ms | 11.98 ms | 13.90 ms |
| Detalle | 25 | 2.68 ms | 4.24 ms | 12.69 ms | 15.33 ms |
| Replay sanitizado | 25 | 5.29 ms | 6.52 ms | 14.65 ms | 17.21 ms |
| Restablecer transporte | 5 | 5.41 ms | 8.69 ms | 9.14 ms | 9.25 ms |
| `ResumeMatch` hasta snapshot | 5 | 10.58 ms | 22.26 ms | 24.28 ms | 24.78 ms |

La reconexión separa el establecimiento del transporte de la recuperación del estado. Cada snapshot recuperado coincidió en `matchId` y secuencia con el estado anterior a la desconexión. Al finalizar quedaron cero conexiones en `ConnectionRegistry`.

## Recursos y capacidad observada

| Escenario | CPU aproximada | Pico de working set .NET | Working set final |
| --- | ---: | ---: | ---: |
| A | 7.19 % | 91.88 MiB | 91.82 MiB |
| B | 18.40 % | 100.23 MiB | 100.23 MiB |
| C | 20.46 % | 110.15 MiB | 110.26 MiB |
| D | 6.64 % | 111.23 MiB | 111.23 MiB |
| E | 8.04 % | 111.46 MiB | 111.43 MiB |

Entre la línea base y 20 partidas el proceso creció aproximadamente 18.27 MiB, cerca de 0.96 MiB por partida adicional. Es una aproximación contaminada por cachés, clientes del ejecutor e historial retenido durante la misma ejecución; no es una cuota de memoria aislada del servidor. No hubo señales de saturación a 20 partidas y el escenario C completó sin errores, por lo que el punto de ruptura queda **no determinado**. Aumentar la carga más allá de lo solicitado habría sido una extrapolación innecesaria y menos segura.

## Consistencia y regresión

Después de la carga, antes de destruir el volumen aislado:

- 37 partidas y 149 eventos persistidos.
- 0 partidas activas, 0 planes pendientes, 0 comandos sin resultado.
- 0 pares duplicados de `match_id` y `sequence_number`.
- 0 conexiones registradas.
- El contenedor y el volumen de la prueba fueron eliminados.

Validaciones posteriores:

- Build Release del ejecutor: correcto, 0 errores y 0 advertencias.
- Suite .NET: 152/152 (25 dominio, 28 servidor, 99 PostgreSQL real) aprobadas.
- Frontend: 3/3 pruebas aprobadas; ESLint correcto; build Vite correcto.
- Vite informó únicamente que el bundle principal de 532.52 kB supera su umbral informativo de 500 kB.
- `git diff --check`: correcto.

## Interpretación y mejoras recomendadas

No se justifica modificar el servidor con estos resultados. La ruta crítica de ronda mantuvo margen de 98.04 ms en p95 incluso con 20 partidas, sin errores ni pérdida o duplicación observable.

1. **Alta utilidad, bajo riesgo:** repetir en un entorno de staging equivalente a Container Apps usando WebSockets reales y telemetría de CPU/memoria por contenedor. Esto cerraría la principal limitación sin cambiar código.
2. **Alta utilidad, bajo riesgo:** ejecutar una prueba sostenida de 5–10 minutos y crecimiento escalonado superior a 20 partidas en un entorno autorizado para localizar el punto de ruptura y observar pools/conexiones.
3. **Utilidad media, bajo riesgo:** instrumentar métricas de duración de transacciones, pool Npgsql y outbox para explicar la pendiente de `match.start` y de ronda sin perfilar datos sensibles.
4. **Utilidad media, riesgo medio:** solo si una prueba posterior confirma degradación, revisar el trabajo transaccional de inicio de partida y los viajes a PostgreSQL. No se recomienda refactorizarlo con la evidencia actual.
5. **Separado del backend:** aplicar división de código al bundle web podría eliminar la advertencia de Vite, pero no afecta la latencia medida de las partidas.

## Reproducción y archivos

Ejecutar desde la raíz en PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\performance\run.ps1
```

Archivos generados:

- `scripts/performance/run.ps1`: ciclo reproducible de preparación, ejecución y limpieza.
- `scripts/performance/Game.Performance/`: ejecutor .NET Release.
- `scripts/performance/results/phase5a-results.json`: 375 muestras individuales, agregados, recursos y validación de base de datos.
- `scripts/performance/README.md`: instrucciones y límites del método.
- `docs/phase5a-performance-validation.md`: este informe.

El archivo JSON no contiene JWT, contraseñas, cadenas de conexión ni datos personales. Los documentos operativos `phase3d*` y `evidencias/` permanecieron intactos y sin rastrear.
