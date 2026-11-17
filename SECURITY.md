# Política de seguridad

BackupHub guarda credenciales de bases de datos, servidores y nubes, y maneja copias de datos de producción. Por eso, los problemas de seguridad se reportan **en privado**.

## Cómo reportar una vulnerabilidad

1. **No abras un issue público.**
2. Usa **[Report a vulnerability](../../security/advisories/new)** en la pestaña *Security* del repositorio (GitHub Private Vulnerability Reporting).
3. Incluye la versión o el commit, los pasos para reproducirlo, el impacto que esperas y, si la tienes, una propuesta de corrección.

Respondemos en un plazo de **5 días hábiles**. Coordinamos contigo la fecha de publicación y te damos crédito en el aviso, salvo que prefieras quedar en el anonimato.

## Versiones con soporte

Solo la rama `main` recibe correcciones de seguridad.

## Alcance

Entran en el alcance, entre otros:

- Acceso a datos, respaldos o credenciales de otro tenant.
- Exposición de secretos (contraseñas de conexiones, llaves de Data Protection, tokens) en la interfaz, los logs o las respuestas HTTP.
- Escalada de permisos (de lector a administrador del tenant, o de un tenant a la administración de la plataforma).
- Path traversal en orígenes, destinos, el explorador de carpetas o la restauración.
- Fallos en el cifrado o en la verificación de integridad de los artefactos.

Quedan fuera del alcance: las instalaciones sin HTTPS, con el registro abierto a internet o con el origen "Carpeta local" en manos de usuarios no confiables. Esos riesgos ya están documentados en [docs/seguridad.md](docs/seguridad.md).

## Recomendaciones para quien despliega

Lee [docs/seguridad.md](docs/seguridad.md). En resumen: pon HTTPS delante, respalda y protege el volumen `/data` (contiene las llaves de cifrado) y usa cuentas dedicadas de mínimo privilegio en los orígenes y destinos.
