[← Documentación](README.md)

# 🔐 Seguridad y operación

## Qué protege BackupHub

| Tema | Detalle |
|---|---|
| 🗝️ **Llaves de cifrado** | `/data/keys` contiene las llaves de Data Protection que cifran las contraseñas de orígenes, destinos, conexiones, destinos de restauración y SMTP. Respalda el volumen `/data` completo y protégelo como un secreto. |
| 🙈 **Secretos en la UI** | Las contraseñas guardadas nunca vuelven al navegador: se muestran como "guardada", y dejarlas vacías conserva la anterior. El historial de restauraciones guarda el destino sin credenciales. |
| 🔒 **Contraseña de los artefactos** | Se guarda cifrada en el trabajo. Sin ella, un `.enc` no se puede restaurar: guárdala también fuera de BackupHub. |
| 🧾 **Integridad** | Cada ejecución registra el SHA-256 del respaldo subido. Antes de revisar, descargar o restaurar, se descarga del destino y se verifica; si no coincide, no se entrega. |
| 🏢 **Aislamiento** | Trabajos, ejecuciones, conexiones y restauraciones pertenecen a un tenant, y los servicios filtran siempre por él. Los enlaces de descarga también lo comprueban. |
| 👮 **Permisos** | Revisar, descargar y restaurar respaldos, y gestionar conexiones, es solo para administradores del tenant. Los lectores ven el historial y las bitácoras, pero no el contenido. |
| 🤫 **Secretos de la plataforma** | `Smtp__Password` y `Telegram__BotToken` viven solo en variables de entorno. El cliente HTTP de Telegram no registra URLs, así que el token no aparece en los logs. |
| ⏳ **Tokens de cuenta** | Los enlaces de invitación y recuperación caducan a las 48 h y dejan de valer en cuanto se cambia la contraseña. |
| 💓 **Salud** | `GET /health` responde `200` y lo usa el `HEALTHCHECK` de la imagen. |

## Buenas prácticas

- **Cuentas dedicadas en los orígenes y destinos:** solo lectura para respaldar, escritura solo en la carpeta de respaldos. No reutilices cuentas de personas ni de aplicaciones. Ver [Bases de datos](bases-de-datos.md#usuario-de-solo-lectura-postgresql).
- **Cifrar siempre** los respaldos que contienen datos personales o credenciales, y guardar su contraseña en un gestor de contraseñas.
- **Restaurar en pruebas, no en producción.** Define como destino de restauración predeterminado un entorno de pruebas. "En el origen" es para recuperar de verdad y pide confirmación reforzada.
- **Probar la restauración** periódicamente: un respaldo que nunca se restauró no está probado.
- **No exponer bases de datos a internet**, tampoco las copias de prueba restauradas: contienen datos reales. Usa VPN o túnel SSH.
- **HTTPS** delante de BackupHub (ver [Despliegue](despliegue.md)) y `Registration__Mode=Disabled` si el servidor es accesible desde redes que no controlas.
- **Origen "Carpeta local":** lee el sistema de archivos del contenedor, incluido `/data` con las llaves. Solo deben usarlo administradores de confianza.

## Operación

| Tema | Detalle |
|---|---|
| 🐘 **Versión de `pg_dump`** | Debe ser igual o mayor que la de tus servidores PostgreSQL (argumento `PG_MAJOR` del `Dockerfile`, por defecto 17). |
| 💽 **Espacio temporal** | `Backup__WorkingDirectory` guarda cada respaldo mientras se arma, la caché de descargas (2 h) y las restauraciones en curso. Ver [Despliegue](despliegue.md#espacio-en-disco-y-red). |
| 🔁 **Reinicios** | Al arrancar, las ejecuciones y restauraciones que quedaron a medias se marcan como fallidas; una restauración interrumpida puede haber dejado el destino a medias. |
| 🧹 **Historial** | Las ejecuciones se conservan `Backup__RunHistoryDays` días. Una ejecución se puede eliminar a mano; su respaldo en el destino no se toca. |
