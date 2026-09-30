[← Documentación](README.md)

# ⚙️ Configuración

Todo se configura con variables de entorno (o `appsettings.json`). En `docker-compose.yml` cada variable tiene un equivalente en mayúsculas para usar con un archivo `.env`.

## Aplicación

| Variable | Por defecto | Descripción |
|---|---|---|
| `Backup__DataDirectory` | `/data` (Docker) | Base SQLite y llaves de cifrado. |
| `Backup__WorkingDirectory` | `/tmp/backup-work` | Carpeta temporal donde se arma cada respaldo, la caché de descargas (`artifacts/`, se limpia sola) y las restauraciones en curso (`restores/`). Necesita espacio libre para al menos dos veces el respaldo más grande. |
| `Backup__MaxConcurrentRuns` | `2` | Respaldos ejecutándose en paralelo. |
| `Backup__RunHistoryDays` | `90` | Días de historial de ejecuciones (`0` = para siempre). |
| `Backup__DisplayTimeZone` | UTC | Zona horaria para mostrar fechas, p. ej. `America/Mexico_City` o `Europe/Madrid`. |
| `Backup__PublicUrl` | — | URL pública, p. ej. `https://respaldos.empresa.com`. Se usa en los enlaces de invitación y recuperación; si falta, se usa la URL con la que se abrió la app. |

> [!TIP]
> Detrás de un proxy inverso define siempre `Backup__PublicUrl`, para que los enlaces de los correos apunten a la dirección pública.

## Primer arranque

Solo se aplican si la base no tiene usuarios.

| Variable | Por defecto | Descripción |
|---|---|---|
| `Bootstrap__TenantName` | `Principal` | Nombre del primer tenant. |
| `Bootstrap__AdminEmail` | `admin@backup.local` | Correo del super administrador inicial. |
| `Bootstrap__AdminPassword` | — | Si queda vacía se genera una y se escribe en el log. |

## 📝 Registro

| Variable | Por defecto | Descripción |
|---|---|---|
| `Registration__Mode` | `Enabled` | `Enabled`: cualquiera puede registrarse (ver [Registrarse](usuarios-y-tenants.md#registrarse)) · `Disabled`: solo los administradores crean cuentas. Los valores anteriores `Approval` y `Open` equivalen a `Enabled`. |

## 📧 SMTP de la plataforma <sub>(opcional)</sub>

Servidor de correo del operador. Se usa para **invitaciones y recuperación de contraseña**, y para las **alertas** de los tenants que elijan "Usar el servidor de la plataforma".

| Variable | Por defecto | Descripción |
|---|---|---|
| `Smtp__Host` | — | Servidor SMTP. Si está vacío, la plataforma no tiene SMTP. |
| `Smtp__Port` | `587` | |
| `Smtp__Security` | `Auto` | `Auto`, `StartTls` (587), `SslOnConnect` (465) o `None` (25). |
| `Smtp__Username` / `Smtp__Password` | — | Vacíos si el servidor no pide autenticación. |
| `Smtp__FromAddress` | — | Remitente, p. ej. `respaldos@empresa.com`. Obligatorio. |
| `Smtp__FromName` | `BackupHub` | Nombre visible del remitente. |

## ✈️ Telegram <sub>(opcional)</sub>

| Variable | Descripción |
|---|---|
| `Telegram__BotToken` | Token del bot creado con [@BotFather](https://t.me/BotFather). Vacío = Telegram deshabilitado. |

## 🪝 Webhook <sub>(opcional)</sub>

| Variable | Descripción |
|---|---|
| `Notifications__Webhook__Url` | Recibe un `POST` JSON al terminar cada ejecución (Teams, Slack, n8n…). Es global: aplica a todos los tenants. |
| `Notifications__Webhook__OnlyOnFailure` | `true` para avisar solo de fallos. |
