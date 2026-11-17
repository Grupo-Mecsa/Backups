[← Documentación](README.md)

# 🗄️ Bases de datos

Cada trabajo de base de datos respalda **una base** (en MySQL, una o varias) con la herramienta nativa del motor. El archivo resultante se comprime, se cifra y se sube al destino como cualquier otro respaldo.

| Motor | Herramienta | Respaldo | Restaurar desde la web |
|---|---|---|:---:|
| PostgreSQL | `pg_dump` | `.dump` (custom), `.sql` (plain) o `.tar` | ✅ |
| MySQL / MariaDB | `mysqldump` | `.sql` | ✅ |
| SQL Server | `BACKUP DATABASE` | `.bak` | — |
| Azure SQL Database (o SQL Server) | `sqlpackage /Action:Export` | `.bacpac` | ✅ |
| MongoDB | `mongodump` | `.archive` | — |
| SQLite | API de respaldo en línea (copia consistente) | `.db` | — |

## Elegir bases de datos y esquemas

Junto a los campos de base de datos y esquemas hay un botón **Elegir**. Consulta el servidor con los datos de acceso del trabajo (o de su [conexión](conexiones.md)) y muestra una lista con buscador.

| Motor | Campo | Selección |
|---|---|---|
| PostgreSQL | Base de datos | una |
| PostgreSQL | Esquemas | varios (ninguno marcado = todos) |
| MySQL / MariaDB | Bases de datos | varias (ninguna marcada = todas) |
| SQL Server | Base de datos | una |

En PostgreSQL solo aparecen las bases a las que el usuario puede conectarse y los esquemas en los que tiene permiso de uso (`USAGE`), así no se eligen objetos que luego harían fallar el respaldo. Para listar esquemas, primero indica la base de datos.

Los esquemas con mayúsculas o caracteres especiales (`"Ventas"`, `"mi-app"`) se escriben tal cual, sin comillas: BackupHub las agrega cuando `pg_dump` las necesita. La bitácora muestra qué esquemas se respaldan (`Esquemas: …`).

## Usuario de solo lectura (PostgreSQL)

Para respaldar no hace falta el usuario administrador: basta uno de **solo lectura**, dedicado a los respaldos. Ejecuta esto como propietario de las tablas o como administrador, una vez por cada esquema a respaldar:

```sql
CREATE ROLE respaldos LOGIN PASSWORD 'una-contraseña-larga-y-única';

GRANT USAGE ON SCHEMA public TO respaldos;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO respaldos;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO respaldos;
-- Para que las tablas y secuencias que se creen después también sean legibles:
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO respaldos;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO respaldos;
```

- **Esquemas con mayúsculas:** van entre comillas dobles en SQL (`"Ventas"`). Sin comillas, PostgreSQL los pasa a minúsculas y no los encuentra.
- **Secuencias:** `pg_dump` también las lee. Sin `SELECT` sobre ellas, falla con `permission denied for sequence …`.
- **Si falta un permiso,** la bitácora del respaldo muestra los `GRANT` exactos para los esquemas del trabajo.
- **Sin efectos para los demás:** estos permisos solo afectan al usuario `respaldos`, que solo puede leer. `pg_dump` tampoco bloquea las lecturas ni escrituras de tus aplicaciones; sí impide cambios de estructura (migraciones, `ALTER TABLE`) mientras dura. Programa los respaldos en horarios de poca actividad.

### Tablas con seguridad por fila (RLS)

Si una tabla tiene RLS, `pg_dump` se niega a copiarla con un usuario que no pueda saltarse sus políticas: `query would be affected by row-level security policy for table …`. Lo hace a propósito, para no dejar un respaldo incompleto sin avisar. Hay tres soluciones:

1. **Recomendada:** que el usuario de respaldo ignore RLS.

   ```sql
   ALTER ROLE respaldos BYPASSRLS;
   ```

2. **Si tu servicio no permite `BYPASSRLS`:** una política de solo lectura para ese usuario en cada tabla con RLS. Solo afecta a `respaldos`, porque las políticas permisivas se suman por rol. Hay que volver a ejecutarla cuando se creen tablas nuevas con RLS:

   ```sql
   DO $$
   DECLARE t record;
   BEGIN
     FOR t IN SELECT schemaname, tablename FROM pg_tables
              WHERE rowsecurity AND schemaname IN ('public')
     LOOP
       EXECUTE format('DROP POLICY IF EXISTS respaldo_lectura ON %I.%I', t.schemaname, t.tablename);
       EXECUTE format('CREATE POLICY respaldo_lectura ON %I.%I FOR SELECT TO respaldos USING (true)', t.schemaname, t.tablename);
     END LOOP;
   END $$;
   ```

   Estas políticas viajan dentro del respaldo. Al restaurar en otro servidor donde no exista `respaldos`, aparecerán como advertencias `role "respaldos" does not exist` (inofensivas).

3. **Respaldar con un usuario que ya tenga `BYPASSRLS`**, por ejemplo el propietario de las tablas.

> [!WARNING]
> No agregues `--enable-row-security` para evitar el error: `pg_dump` respaldaría solo las filas visibles para ese usuario, que pueden ser ninguna, y el respaldo parecería correcto.

> [!CAUTION]
> Un usuario con `BYPASSRLS` puede leer **todas** las filas, incluidas las sensibles (credenciales, datos personales). Por eso debe ser una cuenta dedicada que no use ninguna persona ni aplicación, y los respaldos deben ir **cifrados**.

## Servicios gestionados

Al crear un trabajo o una conexión hay tipos propios para los servicios más usados. Usan la misma herramienta que su motor, pero traen los valores por defecto y las ayudas de cada servicio:

| Tipo | Motor | Valores por defecto y notas |
|---|---|---|
| **Supabase** | PostgreSQL | Host del *Session pooler* (`aws-0-<región>.pooler.supabase.com`, puerto 5432), usuario `postgres.<ref>`, SSL `require`, esquema `public`. |
| **Neon** | PostgreSQL | SSL `require`, base `neondb`. Usa el host **sin** `-pooler`. |
| **Azure Database for PostgreSQL** | PostgreSQL | SSL `require`. Permite la IP del servidor de respaldos en *Redes*. |
| **Azure Database for MySQL** | MySQL | Igual que MySQL; Azure exige TLS. |
| **MongoDB Atlas** | MongoDB | URI `mongodb+srv://…`. Permite la IP en *Network Access*. |
| **Azure SQL Database** | SQL Server | `.bacpac` con SqlPackage (ver abajo). |

Un trabajo guardado con el tipo genérico (p. ej. PostgreSQL apuntando a Supabase) sigue funcionando igual; no hace falta cambiarlo.

### Azure SQL Database

Azure SQL no admite `BACKUP DATABASE … TO DISK`, así que se exporta a **`.bacpac`** (esquema y datos) con [SqlPackage](https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage), que trae la imagen Docker (solo x64; se omite con `--build-arg INSTALL_SQLPACKAGE=false`). Sirve también para un SQL Server propio cuando no hay carpeta compartida con el servidor.

| Tema | Qué revisar |
|---|---|
| **Firewall** | Permite la IP del servidor de respaldos en el servidor lógico (*Redes*). |
| **Autenticación** | `SqlPassword` (usuario de SQL) o Microsoft Entra ID: `ActiveDirectoryServicePrincipal` (usuario = Client ID, contraseña = secreto), `ActiveDirectoryManagedIdentity` o `ActiveDirectoryDefault`. |
| **Consistencia** | El `.bacpac` no es transaccional: si la base recibe escrituras durante la exportación puede quedar inconsistente. Para producción con mucha escritura, exporta una copia (`CREATE DATABASE copia AS COPY OF …`). |
| **Restaurar** | SqlPackage solo importa en una base **nueva o vacía**: elige un nombre nuevo o activa *Borrar la base si existe*. En Azure puedes indicar edición y objetivo de servicio (`GP_S_Gen5_2`, `S0`…). |

## PostgreSQL gestionado en la nube

Los servicios gestionados (Supabase, Neon, RDS, Azure Database, Cloud SQL y otros) funcionan igual, con algunos cuidados:

| Tema | Qué revisar |
|---|---|
| **Host** | Usa el que indique el panel del servicio. Algunos ofrecen un host directo y otro a través de un *pooler* de conexiones; si el directo solo tiene IPv6 y tu servidor no, usa el del pooler. |
| **Pooler** | `pg_dump` necesita el **modo sesión**, no el modo transacción (suelen estar en puertos distintos, p. ej. 5432 y 6543). Algunos poolers exigen el usuario con un sufijo, p. ej. `respaldos.identificador`. |
| **SSL** | Normalmente `require`. |
| **Esquemas** | Respalda solo los tuyos. Los esquemas internos de la plataforma no suelen ser legibles para un usuario propio y harían fallar el respaldo. |
| **Argumentos adicionales** | `--no-owner --no-privileges`, para que el respaldo se pueda restaurar en otro servidor sin sus roles. |
| **Versión** | La versión de `pg_dump` debe ser igual o mayor que la del servidor (ver abajo). |

Para restaurar estos respaldos en un PostgreSQL propio, usa la opción **Compatibilidad con PostgreSQL gestionado** (ver [Revisar y restaurar](restaurar.md#postgresql)).

## Errores frecuentes

| Mensaje | Causa | Qué hacer |
|---|---|---|
| `permission denied for table/sequence …` | Falta `SELECT` sobre tablas o secuencias. | Ejecuta los `GRANT` que muestra la bitácora. |
| `query would be affected by row-level security policy` | Tabla con RLS y usuario sin `BYPASSRLS`. | Ver [Tablas con seguridad por fila](#tablas-con-seguridad-por-fila-rls). |
| `no matching schemas were found` | Ninguno de los esquemas del trabajo existe en esa base. | La bitácora lista los esquemas disponibles. Usa **Elegir**. |
| `server version mismatch` | `pg_dump` más antiguo que el servidor. | Sube `PG_MAJOR` en el `Dockerfile` y reconstruye la imagen. |
| `connection … timed out` / `Network is unreachable` | Host o puerto inaccesibles desde el contenedor, o sin IPv6. | Revisa el host, el firewall y, en servicios gestionados, usa el pooler. |

## Versión de las herramientas

La imagen trae los clientes de **PostgreSQL 17** (argumento `PG_MAJOR` del `Dockerfile`), el cliente de MySQL/MariaDB y las MongoDB Database Tools. `pg_dump` de la versión 17 respalda servidores PostgreSQL 17 o anteriores. Para un servidor más nuevo, construye la imagen con un `PG_MAJOR` mayor.
