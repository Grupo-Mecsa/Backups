[← Documentación](README.md)

# ☁️ Nube y unidades en la nube

Todos estos tipos sirven como **origen** (se descarga la carpeta o el bucket y se empaqueta en `.zip`) y como **destino** de los respaldos. Los de archivos (Azure Files, Google Drive, OneDrive, Dropbox, WebDAV) también reciben **restauraciones** de un `.zip`, y su carpeta se elige con el explorador, igual que en FTP o SFTP (ver [Proveedores de archivos](proveedores-archivos.md)).

## Servicios compatibles con S3

Usan la misma implementación que **Amazon S3 / compatible**; cada tipo solo pide lo que identifica al servicio y arma el endpoint solo.

| Tipo | Qué pide | Credenciales |
|---|---|---|
| **Cloudflare R2** | ID de cuenta, jurisdicción (normalmente `default`) | R2 → *Administrar tokens de API* → token con permiso de lectura y escritura. |
| **Backblaze B2** | Región del endpoint (`us-west-004`…) | *Application Keys* → keyID y applicationKey. |
| **Wasabi** | Región del bucket | *Access Keys* de la consola. |
| **Google Cloud Storage** | — | Cloud Storage → *Configuración* → *Interoperabilidad* → clave HMAC de una cuenta de servicio. Clases: STANDARD, NEARLINE, COLDLINE, ARCHIVE. |
| **DigitalOcean Spaces** | Región (`nyc3`, `fra1`…) | *API* → *Spaces Keys*. |
| **Supabase Storage** | ID del proyecto, región | *Storage* → *Settings* → *S3 Connection* → *New access key* (acceso total a Storage). |
| **MinIO** | URL del servidor | *Access Keys* de la consola de MinIO. |

Si un servicio no aparece en la lista, usa **Amazon S3 / compatible** con su *Endpoint personalizado*.

## Azure Files

Recurso compartido de Azure Files por **HTTPS** (no necesita el puerto 445, que muchos proveedores de internet bloquean para SMB). Pide la cadena de conexión de la cuenta de almacenamiento (*Claves de acceso*) y el nombre del recurso compartido, o una **URL SAS** del recurso con permisos de lectura, escritura, eliminación y listado.

## Conectar cuentas desde la web

Google Drive, OneDrive y Dropbox tienen un botón **Conectar con Google / Microsoft / Dropbox** en el formulario de la conexión (o del trabajo):

1. Pulsa el botón: se abre una ventana del servicio para elegir la cuenta y aceptar los permisos.
2. Al aceptar, la ventana se cierra sola y el formulario queda con la cuenta autorizada (*Conectada como …*).
3. **Guarda** la conexión.

El código de autorización se cambia por el token en el servidor (OAuth con PKCE): el refresh token nunca pasa por el navegador, y se guarda cifrado como cualquier otra contraseña.

**Requisitos.** Los servicios solo redirigen a direcciones **https://** o a **localhost**:

| Cómo abres BackupHub | Modo |
|---|---|
| `https://respaldos.empresa.com` (o `http://localhost:…`) | **Automático**. Registra en el servicio el URI `https://respaldos.empresa.com/oauth/callback` (el diálogo lo muestra con un botón para copiarlo). Detrás de un proxy, define `Backup__PublicUrl`. |
| `http://192.168.1.20:8080` u otra IP/http | **Manual**. Registra también `http://localhost` como URI de redireccionamiento. Tras aceptar, el navegador mostrará un error de conexión en `http://localhost/?code=…`: copia esa dirección completa y pégala en el diálogo. |

**Cliente OAuth.** Cada servicio exige una aplicación registrada (Client ID y secreto). El administrador puede configurarla una sola vez para todos (variables `Backup__OAuth__…`, ver [Configuración](configuracion.md#-cuentas-en-la-nube-oauth-opcional)); si no, escribe el Client ID y el secreto en la conexión antes de pulsar **Conectar**.

## Google Drive

Hay dos formas de autenticarse:

| Modo | Para | Cómo |
|---|---|---|
| **OAuth** (predeterminado) | Cualquier cuenta (Gmail o Workspace) | Botón **Conectar con Google**. Los archivos se leen y escriben en el Drive de esa cuenta. |
| **ServiceAccount** | Google Workspace | Cuenta de servicio con su clave JSON. Las cuentas de servicio **no tienen espacio propio**: escribe en una **unidad compartida** (agrega la cuenta como *Administrador de contenido* y pon el ID de la unidad) o, con *delegación de todo el dominio*, suplanta a un usuario (campo *Suplantar a*, alcance `https://www.googleapis.com/auth/drive`). |

Los documentos de Google (Docs, Sheets, Slides, Drawings) no son archivos: al respaldarlos se **exportan** a `.docx`, `.xlsx`, `.pptx` y `.pdf`. Google limita la exportación a 10 MB por documento; los que la superen se omiten y quedan en la bitácora. Formularios, accesos directos y sitios se ignoran.

### Cliente OAuth de Google

1. En [Google Cloud Console](https://console.cloud.google.com/) crea (o elige) un proyecto y habilita la **Google Drive API**.
2. *Pantalla de consentimiento de OAuth* (*Google Auth Platform*): tipo **Externo** (o *Interno* si todas las cuentas son de tu Workspace) y **publícala en Producción**: en modo de prueba el token caduca a los 7 días. Mientras Google no verifique la app, al autorizar aparece el aviso *Google no verificó esta app* → *Configuración avanzada* → *Ir a …*; es normal para una app propia.
3. *Clientes* → *Crear cliente* → tipo **Aplicación web**. En *URIs de redireccionamiento autorizados* agrega `https://<tu-backuphub>/oauth/callback` (y `http://localhost` si usarás el modo manual).
4. Copia el Client ID y el secreto en la conexión, o en `Backup__OAuth__Google__ClientId` / `ClientSecret` para todos.

## OneDrive / SharePoint

Usa Microsoft Graph con una aplicación registrada en **Microsoft Entra ID**.

**Cuentas de empresa con permisos de aplicación (recomendado para respaldos desatendidos).** *Registros de aplicaciones* → nueva aplicación → *Permisos de API* → Microsoft Graph → **permisos de aplicación** `Files.ReadWrite.All` (y `Sites.ReadWrite.All` para SharePoint) → *Conceder consentimiento de administrador*. Crea un secreto en *Certificados y secretos* (anota su vencimiento). No hace falta pulsar **Conectar**: deja vacío el refresh token. En el campo **Unidad** escribe:

| Valor | Unidad |
|---|---|
| `usuario@empresa.com` | OneDrive de ese usuario |
| `https://empresa.sharepoint.com/sites/Finanzas` | Biblioteca *Documentos* del sitio |
| `b!AbCd…` | Cualquier unidad por su ID |

**OneDrive personal o de un usuario (permisos delegados).** Registra la aplicación para las cuentas que correspondan (*cuentas personales* para OneDrive personal), agrega la plataforma **Web** con el URI `https://<tu-backuphub>/oauth/callback` (y `http://localhost` para el modo manual), el permiso **delegado** `Files.ReadWrite.All` y un secreto. En el inquilino escribe `consumers` (personal), el de tu empresa o déjalo vacío (`common`), y pulsa **Conectar con Microsoft**. Microsoft limita estos tokens a **90 días**: para respaldos desatendidos de larga duración, prefiere los permisos de aplicación.

## Dropbox

1. En [dropbox.com/developers/apps](https://www.dropbox.com/developers/apps) crea una app *Scoped access* (carpeta de la app o Dropbox completo) y marca los permisos `files.metadata.read`, `files.content.read` y `files.content.write` (*Permissions* → *Submit*).
2. En *Settings* → *OAuth 2* → *Redirect URIs* agrega `https://<tu-backuphub>/oauth/callback` (y `http://localhost` para el modo manual).
3. Escribe el app key y el app secret en la conexión (o en `Backup__OAuth__Dropbox__…`) y pulsa **Conectar con Dropbox**. El refresh token no caduca.

Con una app de tipo *carpeta de la app*, la raíz `/` es la carpeta `Aplicaciones/<nombre de la app>`.

## WebDAV / Nextcloud

Nextcloud, ownCloud, NAS Synology/QNAP y cualquier servidor WebDAV. La URL es la de la raíz WebDAV del usuario (en Nextcloud: *Archivos* → *Configuración de archivos* → *WebDAV*, p. ej. `https://nube.empresa.com/remote.php/dav/files/usuario/`). Si la cuenta tiene doble factor, usa una **contraseña de aplicación**.

## Errores frecuentes

| Mensaje | Causa | Qué hacer |
|---|---|---|
| `No se pudo obtener el token de acceso … invalid_grant` | Refresh token vencido o revocado (Google en modo de prueba, Microsoft tras 90 días, contraseña cambiada). | Edita la conexión, pulsa **Volver a conectar** y guarda. |
| `redirect_uri_mismatch` (en la ventana de Google) | El URI de redireccionamiento no está registrado tal cual. | Copia el que muestra el diálogo y agrégalo al cliente OAuth. |
| `no entregó un refresh token` | La cuenta ya había autorizado la app y el servicio no repitió el token. | Quita el acceso de la app en la cuenta (Google: *myaccount.google.com/permissions*) y vuelve a conectar. |
| `Service Accounts do not have storage quota` | Cuenta de servicio de Google escribiendo en *Mi unidad*. | Usa una unidad compartida o la suplantación de un usuario. |
| `Credenciales rechazadas` / `Sin permiso` | Claves erróneas o permisos insuficientes (p. ej. falta el consentimiento de administrador en Entra ID). | Revisa los permisos indicados arriba. |
| `429 Too Many Requests` | Límite de velocidad del servicio. | Se reintenta solo, respetando la espera que pide el servicio. |
