namespace Backup.Application.Pipeline;

/// <summary>
/// Revierte las transformaciones de un artefacto (cifrado, compresión) según las extensiones de su nombre, de la
/// última a la primera: "x.zip.gz.enc" → descifrar → descomprimir → "x.zip".
/// </summary>
public interface IArtifactDecoder
{
    /// <summary>Nombre del archivo una vez revertidas las transformaciones que se reconocen.</summary>
    string DecodedName(string fileName);

    /// <summary>Si el nombre tiene alguna transformación reconocida.</summary>
    bool IsTransformed(string fileName) => DecodedName(fileName) != fileName;

    /// <param name="passphrase">Contraseña de cifrado; obligatoria si el archivo termina en ".enc".</param>
    /// <exception cref="InvalidOperationException">Falta la contraseña, no coincide o el archivo está dañado.</exception>
    Task DecodeAsync(string inputFile, string outputFile, string fileName, string? passphrase, CancellationToken cancellationToken);
}
