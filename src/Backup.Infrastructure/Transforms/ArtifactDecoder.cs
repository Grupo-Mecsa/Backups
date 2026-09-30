using System.IO.Compression;
using System.Security.Cryptography;
using Backup.Application.Pipeline;

namespace Backup.Infrastructure.Transforms;

/// <summary>Revierte cifrado (.enc) y compresión (.gz, .br) en el orden inverso al que se aplicaron.</summary>
public sealed class ArtifactDecoder : IArtifactDecoder
{
    private static readonly string[] Known = [BackupEncryption.FileExtension, ".gz", ".br"];

    public string DecodedName(string fileName)
    {
        var name = fileName;
        while (Known.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } extension)
        {
            name = name[..^extension.Length];
        }

        return name;
    }

    public async Task DecodeAsync(string inputFile, string outputFile, string fileName, string? passphrase, CancellationToken cancellationToken)
    {
        var name = fileName;
        var current = inputFile;
        var temporaries = new List<string>();
        try
        {
            while (Known.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } extension)
            {
                name = name[..^extension.Length];
                var next = DecodedName(name) == name ? outputFile : $"{outputFile}.{temporaries.Count}.tmp";
                if (next != outputFile)
                {
                    temporaries.Add(next);
                }

                await DecodeStepAsync(current, next, extension, passphrase, cancellationToken);
                current = next;
            }

            if (current != outputFile)
            {
                File.Copy(current, outputFile, overwrite: true);
            }
        }
        finally
        {
            foreach (var temporary in temporaries)
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task DecodeStepAsync(string input, string output, string extension, string? passphrase, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        if (extension == BackupEncryption.FileExtension)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new InvalidOperationException("El respaldo está cifrado y el trabajo ya no tiene contraseña de cifrado.");
            }

            try
            {
                await BackupEncryption.DecryptAsync(source, target, passphrase, cancellationToken);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "No se pudo descifrar: la contraseña de cifrado actual del trabajo no es la que se usó en este respaldo, o el archivo está dañado.", ex);
            }

            return;
        }

        await using Stream decompressor = extension == ".gz"
            ? new GZipStream(source, CompressionMode.Decompress)
            : new BrotliStream(source, CompressionMode.Decompress);
        try
        {
            await decompressor.CopyToAsync(target, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"No se pudo descomprimir ({extension}): el archivo está dañado.", ex);
        }
    }
}
