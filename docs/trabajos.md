[← Documentación](README.md)

# 📋 Trabajos y ejecuciones

Un **trabajo** define qué se respalda (origen), dónde se guarda (destino), cuándo se ejecuta y con qué opciones. Cada vez que corre genera una **ejecución**, con su bitácora y, si todo fue bien, un **respaldo** en el destino.

## El asistente

| Paso | Contenido |
|---|---|
| **General** | Nombre, descripción y estado (activo o pausado). El nombre define la carpeta del destino. |
| **Origen** | Tipo de origen, [datos de acceso](conexiones.md) y lo propio del trabajo: carpeta y selección, base de datos y esquemas, bucket y prefijo… |
| **Destino** | Dónde se guardan los respaldos. |
| **Programación** | Cuándo se ejecuta (ver abajo). |
| **Opciones** | Compresión, cifrado y retención. |
| **Restauración** | Opcional: destino predeterminado para [restaurar](restaurar.md) sus respaldos. |
| **Resumen** | Revisión final y errores pendientes. |

- **Probar conexión** valida los datos de acceso antes de guardar.
- **Explorar** recorre las carpetas del servidor.
- **Elegir** consulta las bases de datos o esquemas disponibles.
- En un trabajo existente, **Guardar** está disponible en cualquier paso.

## Programación

| Opción | Se configura |
|---|---|
| Manual | Sin horario: solo se ejecuta con **Ejecutar ahora**. |
| Cada hora / Cada 6 horas | Nada más. |
| Diario | La hora. |
| Semanal | Uno o varios días (Lun a Dom) y la hora. |
| Mensual | El día del mes (1 a 31) y la hora. Los meses sin ese día se saltan. |
| Personalizada | Una expresión cron de 5 campos, p. ej. `30 1 * * 1-5` (lunes a viernes a la 01:30). |

La **zona horaria** define cómo se interpreta la hora. Debajo se muestran las próximas cuatro ejecuciones para comprobarlo.

## Opciones

| Opción | Detalle |
|---|---|
| Compresión | gzip, brotli o ninguna. Los dumps `custom` de PostgreSQL ya vienen comprimidos. |
| Cifrado | AES-256 + HMAC-SHA256 con una contraseña de al menos 8 caracteres. **Guárdala también fuera de BackupHub.** |
| Retención | Conservar los últimos *N* respaldos y/o los de los últimos *N* días. Se aplica en el destino después de cada respaldo correcto. |

## Acciones sobre un trabajo

En **Trabajos**, cada tarjeta permite:

| Acción | Detalle |
|---|---|
| **Ejecutar ahora** / **Cancelar** | Lo encola de inmediato, o cancela la ejecución en curso. |
| Activar / pausar | Un trabajo pausado no se ejecuta según su horario. |
| **Editar** | Abre el asistente. |
| **Duplicar** | Crea una copia completa ("Nombre (copia)", con conexiones, contraseñas, horario, cifrado y retención). La copia queda **pausada** y se abre en el editor para revisarla. |
| **Eliminar** | Borra el trabajo. Los respaldos ya guardados en el destino no se tocan. |

## Estados de una ejecución

| Estado | Significado |
|---|---|
| **En curso** | Se está ejecutando. La bitácora se actualiza en vivo. |
| **Correcto** | El respaldo se subió completo. |
| **Con advertencias** | El respaldo se subió, pero se omitió algo que no se pudo leer (un archivo que desapareció, una carpeta sin permiso…). La bitácora lo detalla con `WRN Omitido …`. Las alertas lo tratan como un fallo. |
| **Falló** | No hay respaldo. El mensaje de error y el detalle técnico explican la causa. |
| **Cancelado** | Se canceló a mano. |

Si el servicio se reinicia a mitad de una ejecución, esta queda como **Falló** con el aviso "Interrumpido por reinicio del servicio".

## Detalle de una ejecución

En **Historial**, al abrir una ejecución:

- **Bitácora:** cada paso, con advertencias en amarillo y errores en rojo. Mientras corre se actualiza cada pocos segundos y escribe líneas de **Progreso** en las descargas largas. Se puede **Copiar** o **Descargar log**.
- **Respaldo** (administradores): descargar, **Ver contenido** y **Restaurar**. Ver [Revisar y restaurar](restaurar.md).
- **Ejecutar de nuevo**, **Editar trabajo** y **Eliminar**. Eliminar borra la ejecución y su bitácora del historial de forma definitiva; el respaldo en el destino no se toca.

El historial se conserva `Backup__RunHistoryDays` días (90 por defecto; ver [Configuración](configuracion.md)).
