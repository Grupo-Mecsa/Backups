using Backup.Application.Runs;

namespace Backup.Tests;

public sealed class LogParserTests
{
    private const string Log = """
        08:31:03 INF Origen: FTP / FTPS → Destino: SMB / Carpeta compartida
        08:31:07 WRN Omitido /mnt/a/FOTOS/Samuel Álvarez ASFAL.png: El servidor listó /mnt/a/FOTOS/Samuel Álvarez ASFAL.png pero responde que no existe.
        Nombre pedido: «Samuel Ã\u0081lvarez ASFAL.png»
        En NLST: «Samuel Ã\u0081lvarez ASFAL.png»
        08:31:08 OMI Omitido /mnt/a/b/MANUAL DE OPERACIÓN.docx: El servidor listó /mnt/a/b/MANUAL DE OPERACIÓN.docx pero responde que no existe.
        08:31:09 OMI Carpeta omitida (aparece en el listado pero el servidor no deja abrirla): /mnt/a/Privado
        08:31:10 WRN Nombre con '..' copiado tal cual (revisar): /mnt/a/x..docx
        08:31:11 ERR 'pg_dump' terminó con código 1.
                 Detalle técnico:
                   System.Exception
        """;

    [Fact]
    public void Groups_lines_into_entries_with_levels_and_details()
    {
        var parsed = LogParser.Parse(Log);

        Assert.Equal(6, parsed.Entries.Count);
        Assert.Equal(LogLevelKind.Omitted, parsed.Entries[1].Level); // WRN "Omitido" de bitácoras anteriores
        Assert.Equal(2, parsed.Entries[1].Details.Count);
        Assert.Equal(LogLevelKind.Warning, parsed.Entries[4].Level);
        Assert.Equal(LogLevelKind.Error, parsed.Entries[5].Level);
        Assert.Equal(2, parsed.Entries[5].Details.Count);
    }

    [Fact]
    public void Summarizes_omitted_items_by_reason()
    {
        var parsed = LogParser.Parse(Log);

        var files = Assert.Single(parsed.Omitted, g => g.Items.Count == 2);
        Assert.Equal("El servidor los lista, pero no deja descargarlos", files.Reason);
        Assert.NotNull(files.Hint);
        Assert.Equal("Samuel Álvarez ASFAL.png", files.Items[0].Name);
        Assert.Equal("/mnt/a/FOTOS", files.Items[0].Folder);

        var folders = Assert.Single(parsed.Omitted, g => g.Items.Count == 1);
        Assert.Equal("/mnt/a/Privado", folders.Items[0].Path);
    }
}
