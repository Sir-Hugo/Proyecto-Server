# Bot local mínimo

Este ejecutor levanta PostgreSQL 17 en un proyecto Docker aislado, aplica las migraciones reales y aloja `Game.Server` mediante `TestServer`. Dos bots se autentican con JWT efímeros firmados dentro del proceso, crean una sala privada, se unen, marcan readiness, eligen nickname y envían planes por el Hub real hasta que la partida termina.

La estrategia básica gasta toda la energía disponible en ataques normales. Es simple, pero genera únicamente planes legales y no requiere intervención entre rondas.

Desde la raíz del repositorio:

```powershell
.\scripts\bot\run.ps1
```

Requisitos: .NET 10, Docker y PowerShell. El script genera la contraseña PostgreSQL en memoria, escribe el archivo temporal fuera del repositorio y elimina contenedor, volumen y archivos al terminar.

## Validación realizada

El 12 de noviembre de 2026 se ejecutó el script completo: los dos bots iniciaron una partida, enviaron planes aceptados durante seis rondas, recibieron secuencias consecutivas, alcanzaron un estado terminal y verificaron el detalle persistido mediante `/api/history/{matchId}`. Los críticos aleatorios pueden cambiar la cantidad de rondas en ejecuciones posteriores. La limpieza dejó cero contenedores y cero volúmenes del bot.

## Límites

- Es una demostración local bot contra bot; no entra a Azure ni usa cuentas Auth0 reales.
- SignalR usa Long Polling porque `TestServer` no expone un listener WebSocket. Los métodos, mensajes, validación JWT, REST, persistencia y resolución son los mismos del servidor.
- No es una IA competitiva ni una prueba de rendimiento.
- El ejecutable no acepta una URL de producción, por lo que no puede generar tráfico accidental contra Azure.
