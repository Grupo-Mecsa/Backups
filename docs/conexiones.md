[← Documentación](README.md)

# 🔑 Conexiones

Una **conexión** guarda los datos de acceso de un servidor o servicio para reutilizarlos en varios trabajos: host, puerto, usuario, contraseña, cifrado, etc. Los trabajos que la usan solo guardan lo propio de cada uno: la carpeta, la selección, la base de datos o el prefijo.

Si cambias una conexión (por ejemplo, una contraseña que caducó), el cambio aplica a **todos** los trabajos que la usan.

## Qué guarda cada una

| Proveedor | En la conexión | En cada trabajo |
|---|---|---|
| FTP / FTPS | host, puerto, usuario, contraseña, cifrado, certificado, codificación de nombres | carpeta remota, selección, patrones |
| SFTP | host, puerto, usuario, contraseña, llave privada y su passphrase, huella del host | carpeta remota, selección, patrones |
| SMB | servidor, recurso compartido, dominio, usuario, contraseña | carpeta dentro del recurso, selección, patrones |
| PostgreSQL | host, puerto, usuario, contraseña, SSL, ruta de `pg_dump` | base de datos, formato, esquemas, argumentos |
| MySQL / MariaDB | host, puerto, usuario, contraseña, ruta de `mysqldump` | bases de datos, opciones, argumentos |
| SQL Server | servidor, usuario, contraseña, certificado | base de datos, carpetas de respaldo, opciones |
| MongoDB | URI, ruta de `mongodump` | base de datos, oplog, argumentos |
| S3 y compatibles | bucket, región, access key, secret key, endpoint, path-style | prefijo, selección, clase de almacenamiento |
| Azure Blob | cadena de conexión, contenedor | prefijo, selección, nivel de acceso |

Carpeta local y SQLite no usan conexiones, porque no tienen datos de acceso.

## Crear una conexión

Hay dos formas:

- **Administración → Conexiones → Nueva conexión.** Eliges el tipo, le pones un nombre (por ejemplo «NAS oficina» o «PostgreSQL producción») y completas los datos de acceso. Con **Probar conexión** se valida antes de guardar.
- **Desde el asistente de un trabajo.** En *Origen* o *Destino*, deja **Configurar desde cero**, completa los datos y activa **Guardar estos datos de acceso como conexión reutilizable**. Al guardar el trabajo se crea la conexión y el trabajo queda usándola.

> [!NOTE]
> Algunos proveedores necesitan un dato del trabajo para probar la conexión: PostgreSQL y SQL Server necesitan la base de datos. En ese caso, prueba desde el asistente del trabajo.

## Usarla en un trabajo

En el paso *Origen*, *Destino* o *Restauración*, después de elegir el tipo, aparecen las conexiones guardadas de ese tipo. Al elegir una, el formulario muestra solo los campos propios del trabajo. **Probar conexión**, **Explorar** y **Elegir** usan los datos de la conexión. También se pueden elegir al [restaurar](restaurar.md) en **Otro destino**.

## Seguridad

- Las contraseñas y demás secretos se cifran en la base de datos con las mismas llaves que los trabajos (`/data/keys`).
- Nunca vuelven al navegador: al editar, deja la contraseña vacía para conservar la guardada.
- Las conexiones pertenecen al tenant: un tenant no ve ni usa las de otro.
- La lista de conexiones indica cuántos trabajos usa cada una (como origen, destino o destino de restauración). Una conexión en uso no se puede eliminar: primero cambia esos trabajos a otra conexión o a **Configurar desde cero**.
- Usa cuentas **dedicadas y con el mínimo permiso**: solo lectura para los orígenes, escritura solo en la carpeta de respaldos para los destinos.
- Si se elimina una conexión que un trabajo todavía usaba, la ejecución falla con el mensaje «La conexión guardada que usa este trabajo ya no existe».
