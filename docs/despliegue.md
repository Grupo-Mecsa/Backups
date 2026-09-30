[← Documentación](README.md)

# 🌐 Despliegue en servidor

`deploy/docker-compose.yml` no incluye servicios de prueba y publica el puerto 8090.

```bash
# En tu equipo: construir y copiar la imagen
docker compose build
docker save backuphub:latest | gzip | ssh usuario@servidor 'gunzip | docker load'
scp deploy/docker-compose.yml usuario@servidor:backuphub/

# En el servidor: variables en backuphub/.env y arranque
ssh usuario@servidor 'cd backuphub && docker compose up -d'
```

<details>
<summary><b>📄 Ejemplo de <code>.env</code></b></summary>

<br/>

```dotenv
BACKUP_PUBLIC_URL=https://respaldos.empresa.com
BACKUP_ADMIN_EMAIL=ti@empresa.com
BACKUP_ADMIN_PASSWORD=
TZ=America/Mexico_City

SMTP_HOST=smtp.office365.com
SMTP_PORT=587
SMTP_SECURITY=StartTls
SMTP_USERNAME=respaldos@empresa.com
SMTP_PASSWORD=********
SMTP_FROM=respaldos@empresa.com

TELEGRAM_BOT_TOKEN=123456789:AA...
```

</details>

Las migraciones de base de datos se aplican solas al arrancar. Antes de actualizar, respalda el volumen de datos:

```bash
docker run --rm -v backuphub_backup-data:/data:ro -v "$PWD":/out alpine \
  tar czf /out/backup-data-$(date +%Y%m%d_%H%M%S).tgz -C /data .
```

## Espacio en disco y red

- **Disco:** `Backup__WorkingDirectory` (por defecto `/tmp/backup-work`, dentro del contenedor) necesita espacio para armar cada respaldo, para la caché de descargas y para las restauraciones. Calcula al menos el doble del respaldo más grande. Si es grande, monta un volumen en esa ruta.
- **Red:** el contenedor debe llegar a los orígenes y destinos. Para respaldar o restaurar en otros contenedores del mismo servidor, únelos a la misma red de Docker y usa el nombre del contenedor como host.
- **Bases de datos expuestas:** no publiques puertos de bases de datos a internet para conectarte desde fuera. Usa una VPN o un túnel SSH.

> [!WARNING]
> **HTTPS**: publica la app detrás de un proxy inverso (Caddy, Nginx, Traefik) con certificado y define `BACKUP_PUBLIC_URL` con la URL `https://`. El proxy debe soportar WebSockets (Blazor Server).
