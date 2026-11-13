# Evidencias visuales revisadas

Estas copias fueron seleccionadas desde `evidencias/` sin modificar los originales. La revisión visual no encontró JWT, cookies, contraseñas, cadenas de conexión, correos personales ni identificadores de suscripción en las imágenes incluidas.

| Archivo | Evidencia |
| --- | --- |
| [`openapi-rooms.png`](openapi-rooms.png) | OpenAPI publicado con operaciones de salas y autenticación requerida. |
| [`openapi-history-administration.png`](openapi-history-administration.png) | Rutas de historial, replay y administración publicadas. |
| [`authenticated-match.png`](authenticated-match.png) | Partida real con dos usuarios autenticados y vistas privadas. |
| [`session-recovery.png`](session-recovery.png) | Ambos clientes recuperados en una ronda posterior. |
| [`match-result.png`](match-result.png) | Resultado terminal consistente desde ambas perspectivas. |

No se incorporaron las capturas originales del portal Azure porque muestran correo personal y/o identificador de suscripción. Tampoco se incorporaron las capturas de historial/replay desde DevTools porque muestran correo y cookies de sesión. CI, PostgreSQL privado, Application Insights y alertas se respaldan mediante enlaces y resultados textuales en [`release-readiness.md`](../release-readiness.md), [`phase3b-deployment.md`](../phase3b-deployment.md) y [`phase7b-observability.md`](../phase7b-observability.md).
