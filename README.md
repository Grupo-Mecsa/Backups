# BackupHub

Herramienta de respaldos multiplataforma en .NET 10 + Blazor Server: configura trabajos que toman datos de un **origen**, los comprimen/cifran y los envían a un **destino**, con programación cron y retención.

| Orígenes | Destinos |
|---|---|
| SQL Server, PostgreSQL, MySQL/MariaDB, MongoDB, SQLite | — |
| Carpeta local, FTP/FTPS, SFTP, SMB | Carpeta local, FTP/FTPS, SFTP, SMB |
| S3 y compatibles, Azure Blob | S3 y compatibles, Azure Blob |

## Ejecutar con Docker

```bash
docker compose up -d --build              # solo la app → http://localhost:8080
docker compose --profile demo up -d       # + PostgreSQL, S3 (SeaweedFS) y SFTP de prueba
```

El volumen `backup-data` (`/data`) guarda la base SQLite **y las llaves de cifrado**: si se pierde, las contraseñas guardadas no se pueden descifrar.

### Valores para probar con el perfil `demo`

| Tipo | Configuración |
|---|---|
| Origen PostgreSQL | Host `postgres` · Puerto `5432` · BD `tienda` · Usuario `postgres` · Contraseña `demo1234` |
| Destino S3 | Bucket `respaldos` · Endpoint `http://s3:8333` · Path-style ✓ · Access key `demo` · Secret `demo1234` · Crear bucket ✓ |
| Destino SFTP | Host `sftp` · Puerto `22` · Usuario `demo` · Contraseña `demo1234` · Carpeta `/respaldos` |
| Destino carpeta local | Ruta `/backups` |

## Arquitectura

```
src/
  Backup.Domain              Entidades (BackupJob, BackupRun, RetentionPolicy)
  Backup.Application         Contratos (IBackupSource, IBackupDestination, IConnectionTester...), pipeline y casos de uso
  Backup.Infrastructure      EF Core/SQLite, Data Protection, compresión, AES-256+HMAC, cron, cola, webhooks
  Backup.Providers.*         Plugins: Databases, Cloud, Files
  Backup.Web                 Blazor Server con atomic design (Atoms → Molecules → Organisms → Templates → Pages)
tests/Backup.Tests           Unitarias + extremo a extremo (BACKUP_IT=1 para S3/SFTP del perfil demo)
```

**Agregar un proveedor**: implementa `IBackupSource` o `IBackupDestination` (y opcionalmente `IConnectionTester`), descríbelo con `ProviderDescriptor` y regístralo con `AddBackupSource<T>()` / `AddBackupDestination<T>()`. La UI genera su formulario automáticamente.

## Restaurar un respaldo cifrado

Formato: `"BKE1" | salt | iv | AES-256-CBC | HMAC-SHA256`, llaves derivadas con PBKDF2-SHA256 (210 000 iteraciones). `BackupEncryption.DecryptAsync` verifica la integridad y descifra; luego se descomprime según la extensión (`.gz` / `.br`).

## Usuarios, roles y tenants

Autenticación con ASP.NET Core Identity (cookie). Cada trabajo, ejecución, usuario y configuración de alertas pertenece a un **tenant**; los servicios filtran siempre por el tenant del usuario.

| Rol | Puede |
|---|---|
| **SuperAdmin** | Todo, más crear/editar tenants y cambiar de tenant desde la barra lateral |
| **Admin** | Crear, editar y ejecutar trabajos; gestionar usuarios y alertas de su tenant |
| **Lector** | Ver panel, trabajos e historial |

En el primer arranque se crea el tenant `Bootstrap__TenantName` y el SuperAdmin `Bootstrap__AdminEmail`. Si `Bootstrap__AdminPassword` está vacío, la contraseña se genera y se muestra una sola vez en `docker logs backuphub`.

## Alertas por correo

En **Administración → Alertas por correo** cada tenant configura su SMTP (MailKit: STARTTLS, SSL/TLS o sin cifrado), destinatarios y cuándo avisar (fallos y/o éxitos). La contraseña SMTP se guarda cifrada. Incluye botón de correo de prueba y atajos para Microsoft 365, Gmail, Zoho y SendGrid.

## Despliegue en servidor

`deploy/docker-compose.yml` (sin servicios demo, puerto 8090):

```bash
docker save backuphub:latest | gzip | ssh usuario@servidor 'gunzip | docker load'
scp deploy/docker-compose.yml usuario@servidor:backuphub/
ssh usuario@servidor 'cd backuphub && docker compose up -d'
```

## Pendiente

- Restauración desde la UI.
- HTTPS: publicar detrás de un proxy inverso (Caddy, Nginx, Traefik) con certificado.
