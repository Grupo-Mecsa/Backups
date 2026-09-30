[← Documentación](README.md)

# 👥 Usuarios, roles y tenants

Autenticación con ASP.NET Core Identity (cookie de 12 h con renovación). Cada trabajo, ejecución, conexión, restauración, usuario, configuración de alertas y chat de Telegram pertenece a un **tenant**, y los servicios filtran siempre por el tenant del usuario: **un tenant nunca ve datos de otro**.

| Rol | Puede |
|---|---|
| 👑 **Super administrador** | Todo lo del Administrador, más crear y editar tenants, entrar a cualquiera desde la barra lateral y asignar el rol de super administrador. |
| 🛠️ **Administrador** | Crear, editar, duplicar, ejecutar y eliminar trabajos; gestionar conexiones; revisar, descargar y restaurar respaldos; ver el historial de restauraciones; gestionar usuarios y alertas de su tenant. |
| 👁️ **Lector** | Ver panel, trabajos e historial de ejecuciones con su bitácora. Vincular su propio Telegram. No ve ni descarga el contenido de los respaldos. |

<details>
<summary><b>🔒 Reglas que aplica el sistema</b></summary>

<br/>

- Nadie puede bloquearse, eliminarse ni cambiar su propio rol.
- Cada tenant conserva al menos un administrador activo.
- Solo un super administrador puede crear o modificar cuentas de super administrador.
- 5 intentos fallidos de inicio de sesión bloquean la cuenta 15 minutos.
- Bloquear a un usuario o cambiar su rol cierra sus sesiones abiertas.
- Los usuarios de un tenant deshabilitado no pueden iniciar sesión (sus trabajos programados siguen ejecutándose).

</details>

## Registrarse

En la pantalla de inicio de sesión, **¿No tienes cuenta? Regístrate** abre un formulario con organización, nombre, correo y contraseña. Lo que pasa depende de si la organización ya existe (el nombre se compara sin distinguir mayúsculas):

| Caso | Resultado |
|---|---|
| 🆕 **Organización nueva** | Se crea el tenant y quien se registra es su **primer usuario**: queda aprobado automáticamente como **administrador** y entra de inmediato. |
| 🏢 **Organización existente** | Se crea una **solicitud para unirse** como **lector**. Sus administradores reciben un correo (si hay SMTP) y la aprueban o rechazan en **Usuarios**. Hasta entonces, al iniciar sesión se le indica que está pendiente. |

Nadie que se registre puede ver datos de otra organización sin que un administrador de ella lo apruebe.

> [!CAUTION]
> El administrador de una organización nueva puede crear orígenes de tipo **Carpeta local**, que leen el sistema de archivos del contenedor (incluido `/data`, con las llaves de cifrado). Si el servidor es accesible desde redes que no controlas, usa `Registration__Mode=Disabled`.

## Crear usuarios

En **Administración → Usuarios → Nuevo usuario** hay dos formas de dar acceso:

| Modo | Cómo funciona |
|---|---|
| ✉️ **Invitación por correo** | El usuario recibe un enlace para definir su contraseña. Vale 48 h y es de un solo uso. Requiere un SMTP disponible. |
| 🔑 **Contraseña inicial** | La defines tú y se la comunicas por otro medio. |

El botón ✉ de cada usuario reenvía la invitación (si nunca entró) o manda un enlace para restablecer la contraseña.

## Crear tenants

En **Administración → Tenants → Nuevo tenant** (solo super administradores) puedes crear al mismo tiempo el **administrador inicial** del tenant, con invitación o con contraseña. Para gestionar después los usuarios de otro tenant, pulsa **Entrar** en su tarjeta.

## Recuperar la contraseña

La pantalla de inicio de sesión tiene **¿Olvidaste tu contraseña?**. La respuesta es siempre la misma, exista o no la cuenta, y cada cuenta recibe como mucho un correo cada 2 minutos. Los correos de cuenta salen del SMTP de la plataforma y, si no hay, del SMTP propio del tenant del usuario.

> [!NOTE]
> Si no hay ningún SMTP configurado, un administrador puede fijar una contraseña nueva desde **Usuarios → Editar**.
