using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Backup.Application.Abstractions;
using Backup.Application.Connections;
using Backup.Application.Pipeline;
using Backup.Application.Providers;
using Backup.Application.Security;
using Backup.Domain.Runs;
using Microsoft.Extensions.Options;

namespace Backup.Application.Runs;

/// <param name="Downloadable">Si se puede descargar por separado (archivos de un .zip); las entradas de un dump son solo informativas.</param>
public sealed record ArtifactEntry(string Path, long? Size, DateTimeOffset? Modified, bool Downloadable);

public enum ArtifactContentKind
{
    /// <summary>No se sabe listar (p. ej. .sql, .bak): solo descargar.</summary>
    None,

    /// <summary>Archivos de un .zip, descargables uno a uno.</summary>
    Archive,

    /// <summary>Índice de un dump de PostgreSQL (pg_restore --list): tablas, datos, funciones...</summary>
    DatabaseDump,
}

public sealed record ArtifactContents(ArtifactContentKind Kind, IReadOnlyList<ArtifactEntry> Entries);

public sealed record PreparedArtifact(string FilePath, string DownloadName);

/// <summary>
/// Revisar un respaldo ya subido: lo descarga del destino a una caché temporal, verifica su SHA-256, revierte
/// cifrado y compresión, y lista o extrae su contenido. La caché se limpia sola a las pocas horas.
/// </summary>
public sealed class ArtifactService(
    IRunRepository runs,
    IJobRepository jobs,
    ConnectionResolver connections,
    IProviderRegistry registry,
    IArtifactDecoder decoder,
    IProcessRunner processes,
    IOptions<BackupOptions> options,
    ICurrentUser user)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(2);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private string CacheRoot => Path.Combine(options.Value.WorkingDirectory, "artifacts");

    /// <summary>Si la ejecución dejó un respaldo que se puede revisar.</summary>
    public static bool HasArtifact(BackupRun run) =>
        run.Status is RunStatus.Succeeded or RunStatus.Warning && !string.IsNullOrEmpty(run.ArtifactName);

    public string FileName(BackupRun run) => Path.GetFileName(run.ArtifactName ?? string.Empty);

    public string DecodedName(BackupRun run) => decoder.DecodedName(FileName(run));

    public bool IsTransformed(BackupRun run) => decoder.IsTransformed(FileName(run));

    /// <summary>Contenido del respaldo para la UI (usuario actual).</summary>
    public async Task<ArtifactContents> GetContentsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var run = await GetRunAsync(runId, user.TenantId, cancellationToken);
        var decoded = await DecodedAsync(run, cancellationToken);

        if (decoded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(decoded);
            return new ArtifactContents(ArtifactContentKind.Archive, [.. archive.Entries
                .Where(e => !e.FullName.EndsWith('/'))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ArtifactEntry(e.FullName, e.Length, e.LastWriteTime, Downloadable: true))]);
        }

        if (decoded.EndsWith(".dump", StringComparison.OrdinalIgnoreCase) || decoded.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return new ArtifactContents(ArtifactContentKind.DatabaseDump, await ListDumpAsync(decoded, cancellationToken));
        }

        return new ArtifactContents(ArtifactContentKind.None, []);
    }

    /// <summary>Archivo listo para descargar. El llamador (endpoint HTTP) ya verificó el rol; aquí se aplica el tenant.</summary>
    /// <param name="decoded">True: descifrado y descomprimido. False: tal como está en el destino.</param>
    public async Task<PreparedArtifact> PrepareAsync(Guid runId, Guid tenantId, bool decoded, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, tenantId, cancellationToken);
        return decoded
            ? new PreparedArtifact(await DecodedAsync(run, cancellationToken), DecodedName(run))
            : new PreparedArtifact(await RawAsync(run, cancellationToken), FileName(run));
    }

    /// <summary>Extrae un archivo del .zip del respaldo. El llamador ya verificó el rol; aquí se aplica el tenant.</summary>
    public async Task<PreparedArtifact> ExtractEntryAsync(Guid runId, Guid tenantId, string entryPath, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, tenantId, cancellationToken);
        var decoded = await DecodedAsync(run, cancellationToken);
        if (!decoded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Este respaldo no es un .zip.");
        }

        using var archive = ZipFile.OpenRead(decoded);
        var entry = archive.GetEntry(entryPath) ?? throw new FileNotFoundException("El archivo no está en el respaldo.", entryPath);

        // Nombre propio por extracción: dos descargas simultáneas del mismo archivo no se pisan.
        var target = Path.Combine(Cache(run.Id), "entries", Guid.NewGuid().ToString("N"), Path.GetFileName(entry.FullName));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var input = entry.Open())
        await using (var output = File.Create(target))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        return new PreparedArtifact(target, Path.GetFileName(entry.FullName));
    }

    private async Task<BackupRun> GetRunAsync(Guid runId, Guid tenantId, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, cancellationToken);
        if (run is null || run.TenantId != tenantId)
        {
            throw new KeyNotFoundException("Ejecución no encontrada.");
        }

        return HasArtifact(run) ? run : throw new InvalidOperationException("Esta ejecución no dejó un respaldo.");
    }

    private string Cache(Guid runId) => Path.Combine(CacheRoot, runId.ToString("N"));

    /// <summary>Descarga el artefacto tal como está en el destino y comprueba que coincida con el SHA-256 registrado.</summary>
    private async Task<string> RawAsync(BackupRun run, CancellationToken cancellationToken)
    {
        var raw = Path.Combine(Cache(run.Id), "raw", FileName(run));
        return await WithLockAsync(run.Id, raw, async part =>
        {
            var job = await jobs.GetAsync(run.JobId, cancellationToken)
                ?? throw new InvalidOperationException("El trabajo de este respaldo ya no existe: descárgalo directamente del destino.");
            var resolved = await connections.ResolveAsync(job, cancellationToken);
            if (registry.Find(ProviderRole.Destination, resolved.Destination.ProviderKey) is not IArtifactReader reader)
            {
                throw new InvalidOperationException("Este destino no permite descargar respaldos desde la web.");
            }

            try
            {
                await reader.DownloadAsync(
                    new DestinationContext(new ProviderSettings(resolved.Destination.Settings), NullRunLog.Instance),
                    run.ArtifactName!, part, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"No se pudo descargar del destino: {ex.Message} Si el respaldo es antiguo, puede que la retención ya lo haya borrado.", ex);
            }

            if (run.Sha256 is { Length: > 0 } expected && !string.Equals(await Sha256Async(part, cancellationToken), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El archivo del destino no coincide con el SHA-256 registrado: fue modificado o está dañado.");
            }
        }, cancellationToken);
    }

    /// <summary>Artefacto con cifrado y compresión revertidos (con la contraseña actual del trabajo).</summary>
    private async Task<string> DecodedAsync(BackupRun run, CancellationToken cancellationToken)
    {
        if (!IsTransformed(run))
        {
            return await RawAsync(run, cancellationToken);
        }

        var raw = await RawAsync(run, cancellationToken);
        var decoded = Path.Combine(Cache(run.Id), "decoded", DecodedName(run));
        return await WithLockAsync(run.Id, decoded, async part =>
        {
            var job = await jobs.GetAsync(run.JobId, cancellationToken);
            await decoder.DecodeAsync(raw, part, FileName(run), job?.EncryptionPassphrase, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Genera <paramref name="target"/> una sola vez (en un ".part" que se renombra al terminar), serializado por
    /// ejecución. Aprovecha para borrar de la caché los respaldos revisados hace más de <see cref="CacheLifetime"/>.
    /// </summary>
    private async Task<string> WithLockAsync(Guid runId, string target, Func<string, Task> produce, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            CleanCache(except: runId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.SetLastWriteTimeUtc(Cache(runId), DateTime.UtcNow);
            if (File.Exists(target))
            {
                return target;
            }

            var part = target + ".part";
            try
            {
                await produce(part);
                File.Move(part, target, overwrite: true);
            }
            finally
            {
                File.Delete(part);
            }

            return target;
        }
        finally
        {
            gate.Release();
        }
    }

    private void CleanCache(Guid except)
    {
        if (!Directory.Exists(CacheRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
        {
            if (Path.GetFileName(directory) == except.ToString("N")
                || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < CacheLifetime)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // En uso por otra descarga: se limpiará en la próxima pasada.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Índice de un dump de PostgreSQL: "TABLE public clientes", "TABLE DATA public clientes"...</summary>
    private async Task<IReadOnlyList<ArtifactEntry>> ListDumpAsync(string dump, CancellationToken cancellationToken)
    {
        var listing = dump + ".list.txt";
        try
        {
            await processes.RunAsync("pg_restore", ["--list", dump], listing, cancellationToken: cancellationToken);
            var lines = await File.ReadAllLinesAsync(listing, cancellationToken);
            return [.. lines
                .Where(l => l.Length > 0 && l[0] != ';')
                .Select(l => l[(l.IndexOf(';', StringComparison.Ordinal) + 1)..].Trim().Split(' ', 3))
                .Where(parts => parts.Length == 3)
                .Select(parts => new ArtifactEntry(parts[2], null, null, Downloadable: false))];
        }
        catch (Exception ex) when (ex is ProcessFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"No se pudo leer el índice del dump: {ex.Message}", ex);
        }
        finally
        {
            File.Delete(listing);
        }
    }

    private static async Task<string> Sha256Async(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
