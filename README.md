# BackupHub

Plataforma de respaldos multi-tenant en **.NET 10 + Blazor Server**. Cada trabajo toma datos de un **origen**, los comprime y cifra, y los envía a un **destino**, con programación cron, retención, historial en vivo y alertas por **correo** y **Telegram**.

| Orígenes | Destinos |
|---|---|
| SQL Server, PostgreSQL, MySQL/MariaDB, MongoDB, SQLite | — |
| Carpeta local, FTP/FTPS, SFTP, SMB | Carpeta local, FTP/FTPS, SFTP, SMB |
| S3 y compatibles, Azure Blob | S3 y compatibles, Azure Blob |

**Características principales**

- Asistente para crear trabajos con explorador de carpetas remoto y prueba de conexión.
- Compresión (gzip / brotli) y cifrado AES-256 + HMAC-SHA256 del artefacto.
- Programación cron, ejecución manual, cola con concurrencia limitada y retención por cantidad/antigüedad.
- Multi-tenant con usuarios y roles (ASP.NET Core Identity).
- Alertas por correo (SMTP propio del tenant o de la plataforma), Telegram (chats privados y grupos) y webhook.
- Invitaciones de usuarios y recuperación de contraseña por correo.
- Contraseñas de orígenes/destinos y SMTP cifradas en reposo con Data Protection.

---

## Contenido

1. [Arranque rápido](#arranque-rápido)
2. [Configuración](#configuración)
3. [Usuarios, roles y tenants](#usuarios-roles-y-tenants)
4. [Alertas](#alertas)
5. [Despliegue en servidor](#despliegue-en-servidor)
6. [Seguridad y operación](#seguridad-y-operación)
7. [Arquitectura](#arquitectura)
8. [Desarrollo](#desarrollo)
9. [Restaurar un respaldo](#restaurar-un-respaldo)
10. [Pendiente](#pendiente)

---

## Arranque rápido

### Con Docker

```bash
docker compose up -d --build              # solo la app → http://localhost:8080
docker compose --profile demo up -d       # + PostgreSQL, S3 (SeaweedFS) y SFTP de prueba
```

En el primer arranque se crea el tenant `Principal` y el super administrador `admin@backup.local`. Si no definiste `BACKUP_ADMIN_PASSWORD`, la contraseña se genera y aparece **una sola vez** en el log:

```bash
docker logs backuphub 2>&1 | grep "Administrador inicial"
```

Cámbiala en **Mi cuenta** después de entrar.

> El volumen `backup-data` (`/data`) guarda la base SQLite **y las llaves de cifrado**. Si se pierden las llaves, las contraseñas guardadas no se pueden descifrar. Inclúyelo en tus propios respaldos.

### Valores para probar con el perfil `demo`

| Tipo | Configuración |
|---|---|
| Origen PostgreSQL | Host `postgres` · Puerto `5432` · BD `tienda` · Usuario `postgres` · Contraseña `demo1234` |
| Destino S3 | Bucket `respaldos` · Endpoint `http://s3:8333` · Path-style ✓ · Access key `demo` · Secret `demo1234` · Crear bucket ✓ |
| Destino SFTP | Host `sftp` · Puerto `22` · Usuario `demo` · Contraseña `demo1234` · Carpeta `/respaldos` |
| Destino carpeta local | Ruta `/backups` |

### Sin Docker (desarrollo)

Requiere el SDK de .NET 10. Para los orígenes de bases de datos se necesitan además `pg_dump`, `mysqldump` o `mongodump` en el `PATH`.

```bash
dotnet run --project src/Backup.Web        # → http://localhost:5003
```

Los datos quedan en `src/Backup.Web/data/` salvo que definas `Backup__DataDirectory`.

---

## Configuración

Todo se configura con variables de entorno (o `appsettings.json`). En `docker-compose.yml` cada variable tiene un equivalente en mayúsculas para usar con un archivo `.env`.

### Aplicación

| Variable | Por defecto | Descripción |
|---|---|---|
| `Backup__DataDirectory` | `/data` (Docker) | Base SQLite y llaves de cifrado. |
| `Backup__WorkingDirectory` | `/tmp/backup-work` | Carpeta temporal donde se arma cada artefacto. |
| `Backup__MaxConcurrentRuns` | `2` | Respaldos ejecutándose en paralelo. |
| `Backup__RunHistoryDays` | `90` | Días de historial de ejecuciones (`0` = para siempre). |
| `Backup__DisplayTimeZone` | UTC | Zona horaria para mostrar fechas, p. ej. `America/Costa_Rica`. |
| `Backup__PublicUrl` | — | URL pública, p. ej. `https://respaldos.empresa.com`. **Recomendada detrás de un proxy**: se usa en los enlaces de invitación y recuperación. Si falta, se usa la URL con la que se abrió la app. |

### Primer arranque

| Variable | Por defecto | Descripción |
|---|---|---|
| `Bootstrap__TenantName` | `Principal` | Nombre del primer tenant. |
| `Bootstrap__AdminEmail` | `admin@backup.local` | Correo del super administrador inicial. |
| `Bootstrap__AdminPassword` | — | Si queda vacía se genera una y se escribe en el log. |

Solo se aplican si la base no tiene usuarios.

### SMTP de la plataforma (opcional)

Servidor de correo del operador. Se usa para **invitaciones y recuperación de contraseña**, y para las **alertas** de los tenants que elijan "Usar el servidor de la plataforma".

| Variable | Por defecto | Descripción |
|---|---|---|
| `Smtp__Host` | — | Servidor SMTP. Si está vacío, la plataforma no tiene SMTP. |
| `Smtp__Port` | `587` | |
| `Smtp__Security` | `Auto` | `Auto`, `StartTls` (587), `SslOnConnect` (465) o `None` (25). |
| `Smtp__Username` / `Smtp__Password` | — | Vacíos si el servidor no pide autenticación. |
| `Smtp__FromAddress` | — | Remitente, p. ej. `respaldos@empresa.com`. Obligatorio. |
| `Smtp__FromName` | `BackupHub` | Nombre visible del remitente. |

### Telegram (opcional)

| Variable | Descripción |
|---|---|
| `Telegram__BotToken` | Token del bot creado con [@BotFather](https://t.me/BotFather). Vacío = Telegram deshabilitado. |

### Webhook (opcional)

| Variable | Descripción |
|---|---|
| `Notifications__Webhook__Url` | Recibe un `POST` JSON al terminar cada ejecución (Teams, Slack, n8n…). Es global: aplica a todos los tenants. |
| `Notifications__Webhook__OnlyOnFailure` | `true` para avisar solo de fallos. |

---

## Usuarios, roles y tenants

Autenticación con ASP.NET Core Identity (cookie de 12 h con renovación). Cada trabajo, ejecución, usuario, configuración de alertas y chat de Telegram pertenece a un **tenant**, y los servicios filtran siempre por el tenant del usuario: un tenant nunca ve datos de otro.

| Rol | Puede |
|---|---|
| **Super administrador** | Todo lo del Administrador, más crear y editar tenants, entrar a cualquiera desde la barra lateral y asignar el rol de super administrador. |
| **Administrador** | Crear, editar, ejecutar y eliminar trabajos; gestionar usuarios y alertas de su tenant. |
| **Lector** | Ver panel, trabajos e historial. Vincular su propio Telegram. |

**Reglas que aplica el sistema**

- Nadie puede bloquearse, eliminarse ni cambiar su propio rol.
- Cada tenant conserva al menos un administrador activo.
- Solo un super administrador puede crear o modificar cuentas de super administrador.
- 5 intentos fallidos de inicio de sesión bloquean la cuenta 15 minutos.
- Bloquear a un usuario o cambiar su rol cierra sus sesiones abiertas.
- Los usuarios de un tenant deshabilitado no pueden iniciar sesión (sus trabajos programados siguen ejecutándose).

### Crear usuarios

En **Administración → Usuarios → Nuevo usuario** hay dos formas de dar acceso:

- **Invitación por correo** (requiere un SMTP disponible): el usuario recibe un enlace para definir su contraseña. El enlace vale 48 horas y es de un solo uso.
- **Contraseña inicial**: la defines tú y se la comunicas por otro medio.

El botón ✉ de cada usuario reenvía la invitación (si nunca entró) o manda un enlace para restablecer la contraseña.

### Crear tenants

En **Administración → Tenants → Nuevo tenant** (solo super administradores) puedes crear al mismo tiempo el **administrador inicial** del tenant, con invitación o con contraseña. Para gestionar después los usuarios de otro tenant, pulsa **Entrar** en su tarjeta.

### Recuperar la contraseña

La pantalla de inicio de sesión tiene **¿Olvidaste tu contraseña?**. La respuesta es siempre la misma, exista o no la cuenta, y cada cuenta recibe como mucho un correo cada 2 minutos. Los correos de cuenta salen del SMTP de la plataforma y, si no hay, del SMTP propio del tenant del usuario.

Si no hay ningún SMTP configurado, un administrador puede fijar una contraseña nueva desde **Usuarios → Editar**.

---

## Alertas

Las alertas se envían cuando una ejecución termina: **fallida o cancelada**, y opcionalmente también cuando termina **correctamente**. El envío no retrasa el cierre de la ejecución, y un fallo de envío solo queda en el log.

### Correo

**Administración → Alertas** (administradores del tenant):

1. Activa las alertas y elige cuándo avisar (fallos y/o éxitos).
2. Escribe los destinatarios, separados por coma o uno por línea.
3. Elige el servidor:
   - **Servidor de la plataforma**: aparece solo si el operador configuró `Smtp__*`.
   - **SMTP propio**: servidor, puerto, seguridad, usuario, contraseña y remitente. Hay atajos para Microsoft 365, Gmail/Workspace, Zoho y SendGrid.
4. Pulsa **Enviar correo de prueba** (usa lo que está en pantalla, sin guardarlo) y luego **Guardar**.

La contraseña SMTP se guarda cifrada y nunca vuelve al navegador: si dejas el campo vacío, se conserva la guardada.

| Proveedor | Servidor | Puerto | Seguridad | Nota |
|---|---|---|---|---|
| Microsoft 365 | `smtp.office365.com` | 587 | STARTTLS | El buzón debe tener *SMTP AUTH* habilitado. |
| Gmail / Workspace | `smtp.gmail.com` | 587 | STARTTLS | Requiere una *contraseña de aplicación*. |
| Zoho | `smtp.zoho.com` | 465 | SSL/TLS | |
| SendGrid | `smtp.sendgrid.net` | 587 | STARTTLS | Usuario `apikey`, contraseña = API key. |

### Telegram

Cada usuario (incluidos los lectores) se suscribe a las alertas de su tenant desde Telegram, en un chat privado o en un grupo.

**1. Crear el bot (una vez, lo hace el operador)**

1. En Telegram, abre [@BotFather](https://t.me/BotFather) y envía `/newbot`. Elige un nombre y un usuario terminado en `bot`.
2. Copia el token (`123456789:AA...`) en `Telegram__BotToken` (o `TELEGRAM_BOT_TOKEN` en `.env`) y reinicia el contenedor.
3. En el log debe aparecer `Bot de Telegram conectado: @tu_bot`.

El bot recibe los mensajes con *long polling* (`getUpdates`): **no necesita URL pública ni puertos abiertos**, solo salida HTTPS a `api.telegram.org`. Por eso el token no debe usarse en otra aplicación con webhook ni en otra instancia de BackupHub al mismo tiempo (Telegram respondería 409 y el log lo indica).

**2. Suscribirse (cada usuario)**

1. Ve a **Mi cuenta → Telegram → Conectar Telegram**.
2. Pulsa **Abrir chat con @tu_bot** y luego **Iniciar**, o **Agregar a un grupo** para que el grupo reciba las alertas. Si el enlace no abre, envía al bot el mensaje `/start CÓDIGO` que muestra la pantalla.
3. La página se actualiza sola al vincular. El código es de un solo uso y caduca en 15 minutos.
4. Elige por chat si quieres **fallos**, **éxitos** o ambos, y usa ⚡ para enviar un mensaje de prueba.

En **Administración → Alertas → Telegram** los administradores ven todos los chats del tenant y pueden desvincularlos.

**Comandos del bot**

| Comando | Acción |
|---|---|
| `/start CÓDIGO` | Vincula el chat al tenant del código. |
| `/estado` | Muestra de qué tenants recibe alertas el chat. |
| `/stop` | Deja de recibir alertas en ese chat. |
| `/ayuda` | Explica cómo suscribirse. |

Si alguien bloquea al bot o lo saca de un grupo, la suscripción se elimina automáticamente. Al eliminar un usuario también se eliminan los chats que vinculó.

### Webhook

Con `Notifications__Webhook__Url`, cada ejecución terminada envía un JSON con `text`, `job`, `status`, `startedAt`, `finishedAt`, `artifact`, `sizeBytes`, `sha256` y `error`. El campo `text` sirve directamente para Teams o Slack.

---

## Despliegue en servidor

`deploy/docker-compose.yml` no incluye servicios de prueba y publica el puerto 8090.

```bash
# En tu equipo: construir y copiar la imagen
docker compose build
docker save backuphub:latest | gzip | ssh usuario@servidor 'gunzip | docker load'
scp deploy/docker-compose.yml usuario@servidor:backuphub/

# En el servidor: variables en backuphub/.env y arranque
ssh usuario@servidor 'cd backuphub && docker compose up -d'
```

Ejemplo de `.env`:

```dotenv
BACKUP_PUBLIC_URL=https://respaldos.empresa.com
BACKUP_ADMIN_EMAIL=ti@empresa.com
BACKUP_ADMIN_PASSWORD=
TZ=America/Costa_Rica

SMTP_HOST=smtp.office365.com
SMTP_PORT=587
SMTP_SECURITY=StartTls
SMTP_USERNAME=respaldos@empresa.com
SMTP_PASSWORD=********
SMTP_FROM=respaldos@empresa.com

TELEGRAM_BOT_TOKEN=123456789:AA...
```

Las migraciones de base de datos se aplican solas al arrancar.

**HTTPS**: publica la app detrás de un proxy inverso (Caddy, Nginx, Traefik) con certificado y define `BACKUP_PUBLIC_URL` con la URL `https://` para que los enlaces de los correos sean correctos. El proxy debe soportar WebSockets (Blazor Server).

---

## Seguridad y operación

- **Llaves de cifrado**: `/data/keys` contiene las llaves de Data Protection con las que se cifran las contraseñas de orígenes, destinos y SMTP. Respalda el volumen `/data` completo y protégelo como un secreto.
- **Contraseña de cifrado de los artefactos**: se guarda cifrada en el trabajo. Sin ella, un artefacto `.enc` no se puede restaurar: guárdala también fuera de BackupHub.
- **Secretos de la plataforma** (`Smtp__Password`, `Telegram__BotToken`) viven solo en variables de entorno. El cliente HTTP de Telegram no registra las URLs de las peticiones, así que el token no aparece en los logs.
- **Tokens de cuenta**: los enlaces de invitación y recuperación caducan a las 48 h y dejan de valer en cuanto se cambia la contraseña.
- **Salud**: `GET /health` responde `200` y lo usa el `HEALTHCHECK` de la imagen.
- **Versión de `pg_dump`**: debe ser mayor o igual que la de tus servidores PostgreSQL (argumento `PG_MAJOR` del `Dockerfile`, por defecto 17).

---

## Arquitectura

```
src/
  Backup.Domain              Entidades: BackupJob, BackupRun, Tenant, NotificationSettings, TelegramSubscription
  Backup.Application         Contratos (puertos), pipeline, casos de uso (JobService, TenantService,
                             NotificationService, TelegramService), notificadores de correo y Telegram
  Backup.Infrastructure      EF Core/SQLite, Identity, Data Protection, compresión, AES-256+HMAC, cron, cola,
                             MailKit, cliente de la Bot API de Telegram (long polling), webhook
  Backup.Providers.*         Plugins de origen/destino: Databases, Cloud, Files
  Backup.Web                 Blazor Server con atomic design (Atoms → Molecules → Organisms → Templates → Pages)
tests/Backup.Tests           Unitarias y extremo a extremo
```

**Flujo de una ejecución**: `SchedulerService` (cron) o el botón *Ejecutar* encolan el trabajo en `BackupQueue` → `BackupWorker` llama a `BackupRunner`, que pide los datos al `IBackupSource`, los pasa por las transformaciones (compresión, cifrado) y los entrega al `IBackupDestination` → aplica la retención → cada `IRunNotifier` (UI en vivo, correo, Telegram, webhook) recibe el resultado.

**Agregar un proveedor**: implementa `IBackupSource` o `IBackupDestination` (y opcionalmente `IConnectionTester` / `IFolderBrowser`), descríbelo con `ProviderDescriptor` y regístralo con `AddBackupSource<T>()` / `AddBackupDestination<T>()`. La UI genera su formulario a partir de los `SettingField` del descriptor.

**Agregar un canal de alertas**: implementa `IRunNotifier` y regístralo con `AddSingleton<IRunNotifier, TuNotificador>()`. Los notificadores existentes muestran el patrón: filtran por tenant y preferencias, y envían en segundo plano sin propagar errores.

---

## Desarrollo

```bash
dotnet build Backup.slnx
dotnet test  Backup.slnx                     # unitarias + extremo a extremo con SQLite temporal
BACKUP_IT=1 dotnet test Backup.slnx          # incluye S3/SFTP reales (requiere el perfil demo)
```

Las pruebas usan dobles para SMTP y Telegram (`tests/Backup.Tests/TestDoubles.cs`), así que no envían nada real.

**Migraciones** (requiere `dotnet tool install -g dotnet-ef`):

```bash
dotnet ef migrations add NombreDelCambio -p src/Backup.Infrastructure -s src/Backup.Web -o Persistence/Migrations
```

**Probar Telegram en local**: define `Telegram__BotToken` con un bot de pruebas (no el de producción: dos instancias no pueden hacer polling con el mismo token).

---

## Restaurar un respaldo

Ruta del artefacto en el destino: `{trabajo}/{trabajo}_{yyyyMMdd_HHmmss}{extensión}[.gz|.br][.enc]`, donde la extensión depende del origen: `.zip` (carpetas y archivos), `.sql`/`.dump`/`.tar` (PostgreSQL), `.sql` (MySQL), `.bak` (SQL Server), `.archive` (MongoDB) o `.db` (SQLite).

1. **Descifrar** (`.enc`). Formato: `"BKE1" | salt | iv | AES-256-CBC | HMAC-SHA256`, llaves derivadas con PBKDF2-SHA256 (210 000 iteraciones). `BackupEncryption.DecryptAsync` verifica la integridad antes de descifrar.
2. **Descomprimir** según la extensión: `gunzip` (`.gz`) o `brotli -d` (`.br`).
3. **Restaurar** con la herramienta nativa: `psql`/`pg_restore`, `mysql`, `RESTORE DATABASE` de SQL Server, `mongorestore --archive`, o descomprimir el `.zip`.

---

## Pendiente

- Restauración desde la UI.
- Configurar el bot de Telegram por tenant (hoy hay un bot por plataforma).
- Alerta cuando un trabajo programado no se ejecuta a tiempo.
