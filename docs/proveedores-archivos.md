[← Documentación](README.md)

# 📁 Proveedores de archivos

Carpeta local, FTP/FTPS, SFTP y SMB funcionan como **origen** y como **destino**.

- **Como origen**: descargan la carpeta elegida (con o sin subcarpetas, según los filtros) y la empaquetan en un `.zip`.
- **Como destino**: suben el artefacto y crean las carpetas intermedias que falten.
- **Para restaurar**: reciben el contenido de un respaldo `.zip` (ver [Revisar y restaurar](restaurar.md#carpetas-zip)).

## Rutas

- Las rutas usan `/` como separador en todos los proveedores.
- Una ruta que empieza con `/` es absoluta.
- Una ruta sin `/` inicial se resuelve desde la carpeta en la que el servidor deja al usuario al iniciar sesión, igual que en un cliente FTP o SFTP.

- La carpeta `/` es la raíz del servidor. Si la dejas vacía, se usa la carpeta inicial del usuario.

Por ejemplo, si el servidor deja al usuario en `/srv` y la carpeta del origen es `datos/contabilidad`, se descarga `/srv/datos/contabilidad`.

## Qué respaldar

En el origen, **Archivos y carpetas → Explorar** abre el explorador con casillas: marca lo que se incluye y desmarca lo que se excluye. Gana la regla más específica. Por ejemplo, incluir `srv/datos` y excluir `srv/datos/papelera` respalda todo `datos` menos la papelera.

- Las reglas se guardan con las **rutas absolutas del servidor**, tal como las muestra el explorador, así funcionan aunque el usuario inicie sesión en otra carpeta.
- Al empezar, la bitácora las muestra: `Selección: +srv/datos, -srv/datos/papelera`.
- Además se pueden usar **patrones** de nombre (`*.pdf, *.docx` para incluir; `*.tmp, ~*` para excluir) y decidir si se incluyen las subcarpetas.
- En un destino local, el explorador tiene **Nueva carpeta** para crear la carpeta donde guardar.

## FTP / FTPS

| Campo | Por defecto | Descripción |
|---|---|---|
| Cifrado | `Explicit` | `Explicit` = FTPS (AUTH TLS) · `Implicit` = FTPS implícito (normalmente en el puerto 990) · `None` = FTP plano (no recomendado). |
| Aceptar cualquier certificado | No | Para servidores con certificado autofirmado. |
| Codificación de nombres | `UTF-8` | Cómo se interpretan los nombres con tildes y ñ. Ver [abajo](#codificación-de-nombres). |

### Codificación de nombres

| Valor | Cuándo usarlo |
|---|---|
| `UTF-8` | La mayoría de servidores (NAS Linux, ProFTPD, vsftpd, pure-ftpd). |
| `Latin-1` | Servidores Windows antiguos. |
| `Automática` | Lo que anuncie el servidor en `FEAT`. Si no anuncia UTF-8, las tildes y la ñ se pierden y esos archivos no se pueden descargar. |

Con `UTF-8` o `Latin-1`, BackupHub trata los nombres **byte a byte**: guarda los bytes exactos que devuelve el listado y pide cada archivo con esos mismos bytes. Así funciona aunque un mismo servidor mezcle nombres en UTF-8 y en Latin-1 (archivos creados desde equipos distintos). Para mostrarlos y guardarlos en el `.zip`, cada nombre se lee como UTF-8 si es válido y, si no, como Latin-1. Con `Latin-1` se leen siempre como Latin-1.

Algunos NAS listan los nombres sin convertir, pero **convierten las rutas que reciben** a otro juego de caracteres. Por eso, si una ruta con tildes o ñ no se encuentra, BackupHub prueba otras formas de enviarla: los bytes tal cual, o reconvertidos entre UTF-8 y Windows-1252, Latin-1, CP850 o CP437, en ambos sentidos.
- Se decide archivo por archivo, porque un mismo servidor puede necesitar una forma para los nombres en Latin-1 y otra para los que están en UTF-8.
- La forma que funcionó se prueba primero en el siguiente archivo, y la bitácora lo anota una vez (`El servidor acepta nombres con tildes enviados como «…»`).
- Los nombres que no son UTF-8 válido se muestran como Latin-1, o como CP850 si lo parecen (archivos creados desde equipos DOS o Windows antiguos).

Con `UTF-8` también se envía `OPTS UTF8 ON` al conectar.

Si el archivo sigue sin aparecer, se **omite** y el respaldo continúa con el resto (ver [Elementos omitidos](#elementos-omitidos)). En la bitácora queda un diagnóstico con cómo aparece ese nombre en los listados `MLSD`, `LIST` y `NLST`, la línea original del listado y el tipo (archivo, enlace…). Cada byte no ASCII se muestra como `\uXXXX`: por ejemplo, `\u00F3` es "ó" en Latin-1 y `\u00C3\u00B3` es "ó" en UTF-8.

### Nombres con `..`

Nombres como `..docx` son válidos y se copian tal cual. La bitácora los marca con una advertencia (`Nombre con '..' copiado tal cual (revisar)`) para que puedas revisarlos. Una ruta que saldría de la carpeta de trabajo se omite y también queda en la bitácora.

### Listados vacíos

Algunos servidores devuelven un listado `MLSD` vacío en carpetas que sí tienen contenido. En ese caso BackupHub repite el listado con `LIST`.

## Elementos omitidos

Un archivo que el servidor listó pero ya no existe o no se puede leer no detiene el respaldo: se registra como `WRN Omitido …` y se continúa con los demás. Lo mismo pasa con una carpeta que aparece en el listado pero no se puede abrir.

Si hubo omisiones, la ejecución termina en estado **Con advertencias**: el artefacto se sube igual, pero le faltan esos elementos. Las alertas lo tratan como un fallo (correo, Telegram y webhook avisan aunque solo estén configurados para fallos).

Los errores de conexión o de listado siguen haciendo fallar la ejecución completa.

Mientras descarga, la bitácora escribe cada 10 segundos una línea `Progreso: N archivos (tamaño) · M carpetas pendientes · en <carpeta>`, y la vista en vivo se actualiza cada 3 segundos.

## Solución de problemas

| Mensaje en la bitácora | Causa probable | Qué hacer |
|---|---|---|
| `Omitido …: El servidor listó … pero responde que no existe` en un nombre con tildes | El servidor convierte los nombres de una forma que BackupHub no reconoce. | Envía el diagnóstico completo; prueba también la **Codificación de nombres** `Automática`. |
| El mismo mensaje, en un nombre sin tildes, o el diagnóstico muestra tipo `Link` | El archivo es un enlace simbólico roto, o se borró durante el respaldo. | Revisa el archivo en el servidor. |
| `Carpeta omitida (aparece en el listado pero el servidor no deja abrirla)` | Nombre de carpeta en otra codificación, o falta de permisos. | Revisa la codificación y los permisos del usuario FTP sobre esa carpeta. Su contenido **no** quedó en el respaldo. |
| `No se pudo listar la carpeta …` | Permisos, o el servidor cerró la conexión. | Revisa el detalle técnico debajo del mensaje. |
