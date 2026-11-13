# Ejecutor local de rendimiento

Run from the repository root:

```powershell
.\scripts\performance\run.ps1
```

El script selecciona un puerto loopback libre, genera una contraseña PostgreSQL temporal fuera del repositorio, inicia PostgreSQL 17 mediante un proyecto Compose aislado, aplica las migraciones reales y ejecuta el servidor Release con `Microsoft.AspNetCore.TestHost`. Al terminar elimina el contenedor, volumen y archivo de entorno temporal desde `finally`.

El issuer JWT y la clave sintética existen únicamente dentro del proceso. El ejecutor no contacta credenciales de producción, Auth0 ni Azure. Los límites de seguridad permanecen habilitados. SignalR usa Long Polling porque TestServer no expone un listener WebSocket; los resultados no deben extrapolarse a Azure.

Las muestras y agregados se escriben en `scripts/performance/results/phase5a-results.json`. El archivo se regenera en cada ejecución y está ignorado por Git.
