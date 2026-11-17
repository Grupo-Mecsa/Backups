# Cómo contribuir

¡Gracias por tu interés en BackupHub! Aceptamos reportes de errores, ideas, documentación y código.

## Antes de empezar

- **Errores e ideas:** abre un [issue](../../issues/new/choose) con la plantilla que corresponda. Busca antes si ya existe uno.
- **Vulnerabilidades:** no las publiques; sigue [SECURITY.md](SECURITY.md).
- **Cambios grandes** (un proveedor nuevo, cambios en el esquema o en el formato de los artefactos): abre primero un issue para acordar el enfoque.

## Entorno de desarrollo

Necesitas el SDK de .NET 10 o Docker. En [docs/desarrollo.md](docs/desarrollo.md) están los detalles.

```bash
dotnet build Backup.slnx
dotnet test tests/Backup.Tests
dotnet run --project src/Backup.Web
```

`docker compose up` levanta la app junto con servicios de prueba (PostgreSQL, S3, SFTP…). Sus credenciales (`demo1234`) son solo para desarrollo local.

## Convenciones

- **Idioma:** la interfaz, los mensajes de la bitácora, los comentarios y la documentación están en español.
- **Arquitectura:** respeta las capas descritas en [docs/arquitectura.md](docs/arquitectura.md): `Domain` → `Application` → `Infrastructure` / `Providers.*` → `Web`. Para agregar un proveedor solo hace falta su descriptor: el formulario se genera solo.
- **Secretos:** nunca escribas contraseñas en logs, excepciones ni respuestas a la UI. Los campos `SettingField.Secret` se cifran al guardarse y no vuelven al navegador.
- **Pruebas:** agrega pruebas en `tests/Backup.Tests` para la lógica nueva y para los errores que corrijas.
- **Estilo:** sigue el `.editorconfig` y el estilo del código que ya existe. El build debe compilar sin advertencias nuevas.

## Pull requests

1. Haz un fork y crea una rama desde `main` (`feature/…` o `fix/…`).
2. Haz commits pequeños con mensajes claros.
3. Asegúrate de que `dotnet build` y `dotnet test` pasen.
4. Abre el PR, completa la plantilla y enlaza el issue.
5. Si cambias el comportamiento, actualiza `docs/` y [CHANGELOG.md](CHANGELOG.md).

Al contribuir aceptas que tu aporte se publique bajo la [licencia MIT](LICENSE) y que sigas el [Código de conducta](CODE_OF_CONDUCT.md).
