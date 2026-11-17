# Cambios

Los cambios importantes de cada versión se registran aquí. El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/).

## [Sin publicar]

### Agregado
- Alertas por WhatsApp vía WAHA: la plataforma configura `WhatsApp__BaseUrl`, `WhatsApp__ApiKey` y `WhatsApp__Session`; cada tenant elige números o grupos, cuándo avisar, y puede enviar un mensaje de prueba. El mensaje incluye el diagnóstico del error.
- Limpiar el historial de ejecuciones: solo fallidas y canceladas, o todas; por antigüedad y por trabajo, mostrando cuántas se borrarán. Nunca toca las ejecuciones en curso ni los respaldos en los destinos.
- Bitácora interpretada: entradas con hora y nivel, filtros (errores, advertencias, omitidos, información) con contadores, búsqueda, diagnósticos plegables, seguimiento en vivo y un resumen de los elementos omitidos agrupados por causa, con sugerencia y enlace a cada entrada. Lo omitido se registra con su propio nivel (OMI).
- Vista de lista para los trabajos (además de tarjetas), ordenable por nombre, próxima y última ejecución, con las mismas acciones. La vista elegida se recuerda en el navegador.
- Archivos para publicar el proyecto como open source: política de seguridad, guía de contribución, código de conducta, plantillas de issues y PR, y `deploy/.env.example`.

### Cambiado
- La bitácora ya no repite el mensaje de error en el «Detalle técnico»: ahí solo quedan los tipos de excepción y las trazas.
- Configuración de orígenes y destinos: el proveedor elegido se muestra en una barra compacta con «Cambiar», los campos se agrupan en «Conexión» y «Para este trabajo», y «Probar conexión» queda al final del formulario.

- Errores de las ejecuciones y restauraciones explicados: en lugar del mensaje técnico se muestra qué pasó y qué hacer (RLS, credenciales, recurso SMB inexistente, timeouts, DNS, certificados, nube…). El mensaje original queda plegado en «Mensaje técnico». Las alertas de correo y Telegram incluyen el mismo diagnóstico.
- El error guardado incluye las excepciones internas (ya no queda solo «See InnerException for more info»).

### Corregido
- Orígenes de archivos (FTP, SFTP, SMB): el .zip se escribe directo en el paquete final (compresión, cifrado y SHA-256) a medida que se descarga cada archivo. Antes quedaban a la vez en disco la carpeta descargada, el .zip y el paquete (hasta 3 veces el tamaño); ahora solo el paquete y el archivo en curso. Los formatos ya comprimidos (mp4, jpg, docx, xlsx…) se guardan sin recomprimir.
- FTP: las descargas largas ya no fallan porque se corte la conexión de control. Se envía NOOP cada 60 s, el keepalive TCP está activo y cada archivo se reintenta hasta 3 veces, reconectando, si la conexión se cae.
