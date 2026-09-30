[← Documentación](README.md)

# 🧑‍💻 Desarrollo

```bash
dotnet build Backup.slnx
dotnet test  Backup.slnx                     # unitarias + extremo a extremo con SQLite temporal
BACKUP_IT=1 dotnet test Backup.slnx          # incluye S3/SFTP reales (requiere el perfil demo)
```

Las pruebas usan dobles para SMTP y Telegram (`tests/Backup.Tests/TestDoubles.cs`), así que no envían nada real. Las de extremo a extremo (`EndToEndTests`, `ConnectionTests`) usan el cableado real con SQLite y proveedores locales: respaldos, conexiones, descarga y verificación de artefactos, y restauraciones.

**Migraciones** (requiere `dotnet tool install -g dotnet-ef`):

```bash
dotnet ef migrations add NombreDelCambio -p src/Backup.Infrastructure -s src/Backup.Web -o Persistence/Migrations
```

> [!TIP]
> Para probar Telegram en local usa un bot de pruebas, no el de producción: dos instancias no pueden hacer polling con el mismo token.
