[← Documentación](README.md)

# ♻️ Revisar y restaurar

Las ejecuciones **Correcto** o **Con advertencias** tienen, en su detalle (**Historial → ejecución**), una sección **Respaldo**. Solo la ven los administradores del tenant.

## Revisar y descargar

| Acción | Qué hace |
|---|---|
| **Descargar tal cual** | El archivo exactamente como está en el destino (cifrado o comprimido). |
| **Descargar** *nombre.zip / .dump / .sql…* | El mismo archivo ya descifrado (con la contraseña actual del trabajo) y descomprimido, listo para usar. |
| **Ver contenido** | En un `.zip` (carpetas, FTP, SFTP, SMB, S3, Azure), la lista de archivos con buscador y un botón para descargar cada uno. En un dump de PostgreSQL (`.dump`, `.tar`), el índice de tablas, datos, funciones y secuencias (`pg_restore --list`). |

- **Integridad:** antes de mostrar o entregar nada, BackupHub descarga el respaldo del destino y comprueba que su **SHA-256** coincida con el registrado en la ejecución. Si fue modificado o está dañado, lo avisa y no lo entrega.
- **Caché:** las descargas quedan en una caché temporal (`Backup__WorkingDirectory/artifacts`) que se borra sola 2 horas después del último uso. Un respaldo grande ocupa ahí dos veces su tamaño: tal cual y ya decodificado.
- **Contraseña cambiada:** si la contraseña de cifrado del trabajo cambió después de ese respaldo, el descifrado falla. Descárgalo tal cual y descífralo a mano con la anterior ([ver abajo](#manualmente)).
- **Trabajo eliminado:** sin el trabajo no se conocen los datos del destino, así que el respaldo se descarga directamente desde el destino.

## Restaurar desde la web

### Destino predeterminado del trabajo

En el asistente de cada trabajo, el paso **Restauración** define a dónde se restaura su respaldo por defecto: por ejemplo, un servidor PostgreSQL de pruebas y una base, o una carpeta. Es opcional.

- Solo se ofrecen los tipos que admiten el respaldo que genera el origen: `.zip` para carpetas y nube, dump para PostgreSQL, script para MySQL.
- Puede usar una [conexión guardada](conexiones.md).
- Las contraseñas se guardan cifradas, como las del resto del trabajo.

### Iniciar una restauración

En la sección **Respaldo**, **Restaurar** abre el formulario con tres puntos de partida:

| Punto de partida | Qué carga |
|---|---|
| **Destino del trabajo** | El destino predeterminado. Viene elegido si el trabajo tiene uno. |
| **En el origen** | Los mismos datos de donde se sacó el respaldo: el mismo servidor y carpeta, o el mismo servidor y base. Muestra un aviso y pide confirmación reforzada. |
| **Otro destino** | Configurado a mano, desde cero o con una conexión guardada. |

- **Ajustes:** cualquiera de los tres se puede cambiar antes de confirmar. Las contraseñas guardadas se reutilizan sin mostrarse: deja el campo vacío. Si cambias el tipo de destino, pasa a **Otro destino**.
- **Confirmación:** antes de empezar se pide confirmación. Es más enfática (y el botón es rojo) si se va a borrar o sobrescribir algo, o si se restaura en el origen.
- **Ejecución:** la restauración corre en segundo plano con **bitácora en vivo** y se puede **cancelar**. Lo que ya se restauró queda como está.
- **Historial:** en **Restauraciones** queda cada una, con el respaldo, el destino (sin contraseñas), quién la pidió, su duración, su resultado y su bitácora.
- **Reinicios:** si el servicio se reinicia a mitad de una restauración, queda como **Falló**, con el aviso de que el destino puede haber quedado a medias.

### Carpetas (`.zip`)

Se restauran en **carpeta local, FTP/FTPS, SFTP o SMB**, en la carpeta que elijas con **Explorar**, donde también se pueden crear carpetas. Se respeta la estructura de subcarpetas del respaldo.

| Opción | Por defecto | Detalle |
|---|---|---|
| Sobrescribir archivos existentes | No | Apagado: los archivos que ya existen en la carpeta de destino se dejan como están, y la bitácora cuenta cuántos. |

### PostgreSQL

Se restaura en cualquier servidor PostgreSQL, en la base que elijas (**Elegir** lista las existentes).

| Opción | Por defecto | Detalle |
|---|---|---|
| Crear la base si no existe | Sí | |
| Borrar la base completa y crearla de nuevo | No | Para copias de prueba: elimina la base destino con **todo** su contenido y la crea vacía. Nunca se aplica a la base `postgres`. |
| Compatibilidad con PostgreSQL gestionado | Sí | Crea lo que los respaldos de algunas plataformas gestionadas dan por hecho: el esquema `extensions` (con `pgcrypto` y `uuid-ossp`), los roles `anon`, `authenticated` y `service_role`, y las funciones `auth.uid()`, `auth.role()` y `auth.jwt()` si la base no tiene esquema `auth`. Es inofensivo si no hace falta; los roles se crean sin permiso de inicio de sesión. |
| Borrar antes los objetos existentes (`--clean`) | No | Elimina y vuelve a crear las tablas, vistas y funciones del respaldo que ya existan, con sus datos. Lo que no está en el respaldo se conserva. |
| Sin dueños ni privilegios | Sí | `--no-owner --no-privileges`: evita errores por roles del servidor original que no existen en el destino. |

- **Formatos:** `.dump` y `.tar` se restauran con `pg_restore`; `.sql` con `psql`.
- **Advertencias:** `pg_restore` continúa ante errores no fatales y los resume al final (`errors ignored on restore: N`). En ese caso la restauración termina en **Con advertencias** y los errores quedan en la bitácora. Lo típico es una política o un permiso que menciona un rol que no existe en el destino; las tablas y los datos se restauran igual.
- **Sobre una base con datos:** sin `--clean` ni "Borrar la base completa", los objetos que ya existen chocan (`already exists`) y los datos pueden quedar duplicados. Para una copia limpia, usa una base nueva o una de esas dos opciones.

### MySQL / MariaDB

Se ejecuta el script `.sql` con el cliente `mysql` en el servidor elegido. El respaldo incluye sus `CREATE DATABASE` y `USE`, así que las bases se crean con sus nombres originales.

### No incluido

SQL Server (`.bak`) y MongoDB (`.archive`) todavía no se restauran desde la web: descárgalos y usa la herramienta del motor.

## Manualmente

Ruta del artefacto en el destino:

```
{trabajo}/{trabajo}_{yyyyMMdd_HHmmss}{extensión}[.gz|.br][.enc]
```

| Origen | Extensión |
|---|---|
| Carpetas y nube | `.zip` |
| PostgreSQL | `.dump` · `.sql` · `.tar` |
| MySQL / MariaDB | `.sql` |
| SQL Server | `.bak` |
| MongoDB | `.archive` |
| SQLite | `.db` |

1. **Descifrar** (`.enc`). Formato: `"BKE1" | salt(16) | iv(16) | AES-256-CBC | HMAC-SHA256(32)`, con llaves derivadas por PBKDF2-SHA256 (210 000 iteraciones). `BackupEncryption.DecryptAsync` verifica la integridad antes de descifrar.
2. **Descomprimir** según la extensión: `gunzip` (`.gz`) o `brotli -d` (`.br`).
3. **Restaurar** con la herramienta nativa: `pg_restore` / `psql`, `mysql`, `RESTORE DATABASE` de SQL Server, `mongorestore --archive`, o descomprimir el `.zip`.
