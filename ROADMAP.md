# Ruta de desarrollo del proyecto

Esta ruta organiza el trabajo para terminar primero el servidor local, luego desarrollar la interfaz y finalmente desplegar el sistema en Azure. Se avanzará por pasos y se actualizará este documento conforme completemos cada etapa.

## Estado actual

- El motor de reglas de `src/Game.Domain` implementa las acciones, energía, críticos, bloqueos, ataque especial, límite de 10 rondas, victoria/empate e inactividad.
- El servidor en `src/Game.Server` usa Auth0 y SignalR autenticado. Expone `/health`, OpenAPI 3.1 en `/docs` y REST para crear/listar/unirse/preparar/salir de salas.
- La partida y la cola rápida funcionan con dos jugadores. Los planes simultáneos se validan y resuelven en el servidor; las operaciones se serializan por partida y `requestId` evita aplicar dos veces una jugada, reproduciendo el acuse/resultado ante un reintento idéntico.
- La elección de nickname temporal sucede antes de la ronda 1. El cliente React muestra la batalla, la salud/energía, las acciones, resultados y estado de conexión.
- Partidas, salas, membresías, preparación, plazos, eventos, respuestas idempotentes y outbox usan PostgreSQL. Las conexiones se reconstruyen al autenticar y la cola rápida se invalida/reinscribe tras reinicios.
- La imagen Linux de producción integra ASP.NET Core y el cliente Vite, se ejecuta sin root y expone liveness/readiness. Bicep y los workflows CI/CD de Fase 3A están preparados, pero aún no se ha creado ningún recurso Azure.
- Las salas inactivas expiran tras 5 minutos; una partida termina tras 5 minutos sin planes (empate si ninguno envió, victoria para quien sí envió); la cola rápida espera hasta 4 minutos.
- `docs/asyncapi.yaml` documenta el protocolo AsyncAPI 3.0. Los errores actuales son textos de excepción de SignalR; los mensajes aún no implementados están marcados como planificados. El AsyncAPI está en el archivo, no en la página `/docs`, que corresponde a OpenAPI REST.
- La recuperación del cliente está implementada, pero su prueba en ejecución sigue pendiente: desactivar Wi-Fi no corta `localhost`, y el modo Offline de DevTools tampoco interrumpió la conexión SignalR existente.
- CORS y los orígenes de WebSocket están restringidos mediante `Cors:AllowedOrigins`; en desarrollo se permite `http://localhost:5173`. La región de Azure aún no está desplegada. El repositorio Git existe; estas fases no realizan commits ni push automáticamente.

## Próximo paso

- **Fase 2B implementada:** creación y ciclo de partidas conectados a PostgreSQL, idempotencia y resolución transaccional, asignación activa exclusiva por usuario y eventos reproducibles. Ver `docs/phase2b-validation.md`.
- **Fase 2C implementada:** recuperación de sesiones/salas, reconexión autorizada, resincronización por secuencia, plazos persistentes y entrega al menos una vez mediante outbox. Ver `docs/phase2c-validation.md`.
- **Fase 3A implementada:** contenedor de producción, frontend en el mismo origen, health checks, Bicep parametrizado, job separado de migración y workflows CI/CD inicialmente deshabilitados. Ver `docs/phase3a-validation.md`.
- **Fase 3B pendiente:** cotizar y crear los recursos reales, configurar OIDC/Environments/Auth0/Key Vault, ejecutar la primera migración y medir el despliegue. No iniciarla automáticamente.

## A. Completar y verificar el servidor local

1. [x] **Configurar Auth0 para desarrollo:** crear un tenant, registrar la API del juego y configurar localmente su dominio e identificador. Se comprobó que el hub rechaza tokens ausentes o inválidos y acepta un token temporal válido de la API.
2. [~] **Definir el protocolo inicial en AsyncAPI:** los mensajes implementados tienen versión, esquema y ejemplos; los errores actuales están documentados como textos de excepción de SignalR, y los mensajes todavía no implementados están marcados como planificados. Falta mantener el contrato sincronizado en los siguientes avances.
3. [x] **Implementar salas y emparejamiento:** salas públicas/privadas, membresía, preparación, expiración, inicio de partida y cola rápida en memoria están implementados y comprobados con dos usuarios. El aviso de inicio de una sala llega a ambos jugadores por SignalR. El estado no sobrevive reinicios ni se comparte entre varias instancias.
4. [x] **Conectar SignalR con el caso de uso de partida:** se asocia `sub` de Auth0 al jugador, se valida su pertenencia, se envía el estado privado y se reciben/resuelven rondas sucesivas. Ambos jugadores reciben resultados individuales. Se comprobaron partidas completas con victoria y empate. El cierre por inactividad está implementado; la recuperación tras reconexión sigue pendiente de validación.
5. [x] **Asegurar el procesamiento de jugadas en una instancia:** cada partida se procesa bajo su propio bloqueo; se comprueban secuencia, reglas e idempotencia por `requestId`. Un reintento idéntico recupera el acuse y el resultado ya calculado, mientras que reutilizar el identificador con otro plan se rechaza. La coordinación del estado y las respuestas ya son persistentes en 2B; la difusión entre réplicas sigue pendiente.
6. [x] **Persistir partidas y eventos:** 2B confirma creación, planes, resolución, eventos y resultados privados en PostgreSQL. El estado se consulta por matchId, las rondas se serializan por fila y los requestId sobreviven otra instancia del servicio. Recuperación completa de sesiones queda para 2C.
7. [~] **Implementar conexión y temporizadores:** el servidor termina partidas tras 5 minutos sin actividad: empate si nadie envía plan y victoria para quien sí lo envió si el rival no responde. La recuperación tras desconexión sigue pendiente de una prueba real: el modo Offline de DevTools no interrumpió la conexión SignalR existente. Falta definir heartbeat/período de gracia.
8. **Completar historial y administración:** historial paginado y privado por usuario; operaciones administrativas protegidas por rol y registradas en auditoría.
9. **Endurecer el servidor:** límites por usuario/conexión, tamaño máximo de mensajes, orígenes permitidos, seguridad de logs, encabezados, transporte HTTPS/WSS y aislamiento de fallos entre partidas.
10. [~] **Verificar los flujos locales completos:** salas, emparejamiento, rondas sucesivas, victoria/empate y cierre por inactividad se probaron localmente. Faltan reconexión, reintentos y recuperación/persistencia antes del despliegue.


## B. Desarrollar la interfaz

1. [x] Elegir el framework: React + TypeScript + Vite. La URL local es `http://localhost:5173`; falta definir la URL de producción.
2. [x] Registrar la SPA en Auth0 y configurar sus URL locales de retorno, origen y cierre de sesión. Falta agregar las URL de producción cuando exista el alojamiento.
3. [~] Implementar inicio/cierre de sesión con Auth0 y solicitud de access tokens para la API. La conexión usa el SDK de Auth0; falta verificar el comportamiento de renovación de sesión a largo plazo.
4. [~] Conectar la interfaz a REST y SignalR: recibe estado y resultados privados, envía planes y restaura la suscripción/estado al reconectar. La recuperación aún no está verificada: cliente y servidor se ejecutan en la misma computadora mediante `localhost`, por lo que desactivar el Wi-Fi no corta esa conexión; el modo Offline de DevTools tampoco interrumpió el WebSocket ya establecido. Falta probar un corte real de una sola sesión y mejorar el manejo de errores recuperables.
5. [x] Implementar salas, emparejamiento y pantalla de partida: creación/listado/unión/preparación, flujo de rondas, interfaz de combate y presentación de victoria, empate e inactividad están implementados y probados. Cada participante elige un nickname temporal antes de la ronda 1. Faltaría solo mejorar detalles o estetica de la pantalla.


## C. Desplegar en Azure - Etapa final

1. [x] Preparar la imagen, dimensionamiento inicial, Bicep, Key Vault, identidad administrada, logs y pipelines sin crear recursos.
2. [ ] Cotizar y desplegar la API detrás de HTTPS/WSS; configurar URLs públicas Auth0, OIDC, orígenes y secretos.
3. [ ] Conectar Azure PostgreSQL, ejecutar migraciones mediante el job manual y verificar recuperación con la base real.
4. [ ] Medir monitoreo, costos, despliegue y rollback; configurar presupuesto y alertas.
5. [ ] Verificar el sistema desplegado con dos clientes reales antes de abrirlo a otros jugadores.
6. [ ] Diseñar el escalado horizontal en una fase posterior; requiere difusión SignalR, coordinación de cola/workers y pruebas. Redis no forma parte de 3A.


### Tiempos acordados para salas y emparejamiento

- Una sala expira después de **5 minutos sin actividad de sus miembros**. Se elige 5 minutos para mantener coherencia con el límite de inactividad de una partida y reducir expiraciones mientras los jugadores preparan la sala.
- El emparejamiento rápido espera como máximo **4 minutos**. Si vence ese tiempo sin encontrar rival, la solicitud sale de la cola y el cliente recibe un resultado de espera agotada.



### Decisiones ya acordadas para el protocolo

- Los nombres de mensaje no llevarán la versión como sufijo. La versión viajará en un campo independiente, por ejemplo: `type: "round.plan.submit"` y `version: 1`.
- El evento de resolución comunicará los efectos y el estado resultante de la ronda; no revelará las acciones exactas enviadas por el rival.
- Cada jugador elegirá un nickname temporal al entrar a una partida, antes de iniciar la ronda 1. Debe tener entre 1 y 10 caracteres y solo puede contener letras, números, guion bajo (`_`) o guion medio (`-`).
