using System.Text;
using System.Text.Unicode;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Backup.Providers.Files.Ftp;

/// <summary>FTP / FTPS con FluentFTP.</summary>
internal sealed class FtpSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "ftp",
        "FTP / FTPS",
        role == ProviderRole.Source ? "Descarga una carpeta FTP y la empaqueta en .zip." : "Servidor FTP con cifrado TLS opcional.",
        ProviderCategory.FileTransfer,
        "server",
        [
            SettingField.Text("host", "Host", required: true).ForConnection(),
            SettingField.Number("port", "Puerto", 21).ForConnection(),
            SettingField.Text("user", "Usuario", required: true).ForConnection(),
            SettingField.Secret("password", "Contraseña").ForConnection(),
            SettingField.Text("remotePath", "Carpeta remota", placeholder: "/backups").Browsable(),
            SettingField.Select("encryption", "Cifrado", ["Explicit", "Implicit", "None"], "Explicit",
                "Explicit = FTPS (AUTH TLS). None = FTP plano (no recomendado).").ForConnection(),
            SettingField.Toggle("acceptAnyCertificate", "Aceptar cualquier certificado", false, "Útil con certificados autofirmados.").ForConnection(),
            SettingField.Select("nameEncoding", "Codificación de nombres", ["UTF-8", "Latin-1", "Automática"], "UTF-8",
                "UTF-8 sirve para la mayoría de servidores (NAS Linux); los nombres que no sean UTF-8 válido se leen como Latin-1. Latin-1 para servidores Windows antiguos. Automática = lo que anuncie el servidor (si no anuncia nada, se pierden tildes y ñ).").ForConnection(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var client = new AsyncFtpClient(
            settings.Require("host"),
            settings.Require("user"),
            settings.GetRaw("password") ?? string.Empty,
            settings.GetInt("port", 21));

        client.Config.EncryptionMode = settings.Get("encryption", "Explicit") switch
        {
            "Implicit" => FtpEncryptionMode.Implicit,
            "None" => FtpEncryptionMode.None,
            _ => FtpEncryptionMode.Explicit,
        };
        client.Config.ValidateAnyCertificate = settings.GetBool("acceptAnyCertificate");
        // Los nombres viajan byte a byte (ver RawEncoding): un mismo NAS puede tener nombres en UTF-8 y en Latin-1,
        // y decodificarlos con una sola codificación convierte los otros en "�" imposibles de volver a pedir.
        var nameEncoding = settings.Get("nameEncoding", "UTF-8");
        var raw = nameEncoding is "UTF-8" or "Latin-1";
        if (raw)
        {
            client.Encoding = Exact;
        }

        client.Config.ConnectTimeout = 15_000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        // El detector de traversal busca ".." como subcadena y rechaza nombres legítimos como "..docx".
        // Las rutas salen del listado del propio servidor; el origen avisa en la bitácora de estos nombres
        // y la copia local ya valida que no salga de la carpeta de trabajo.
        client.Config.SanitizeTraversal = false;

        try
        {
            await client.Connect(cancellationToken);
            if (nameEncoding == "UTF-8")
            {
                // Fijar Encoding a mano desactiva el "OPTS UTF8 ON" que FluentFTP manda en modo automático, y hay
                // servidores que traducen el listado a UTF-8 pero no las rutas de RETR/CWD si no se activa.
                await client.Execute("OPTS UTF8 ON", cancellationToken);
            }

            var home = await client.GetWorkingDirectory(cancellationToken);
            return new FtpSession(client, string.IsNullOrWhiteSpace(home) ? "/" : home, raw, preferLatin1: nameEncoding == "Latin-1");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lee cada byte del servidor como un carácter (Latin-1, sin pérdida), así los nombres conservan sus bytes exactos.
    /// Al enviar, <paramref name="outgoing"/> decide qué bytes salen: los mismos, o una conversión que compensa la que
    /// hace el servidor con las rutas que recibe (algunos NAS listan sin convertir pero convierten lo que se les pide).
    /// </summary>
    private sealed class RawEncoding(string label, Func<string, byte[]> outgoing) : Encoding
    {
        public string Label => label;

        public override string EncodingName => $"raw ({label})";

        public override string WebName => "iso-8859-1";

        public override int GetByteCount(char[] chars, int index, int count) => outgoing(new string(chars, index, count)).Length;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            var encoded = outgoing(new string(chars, charIndex, charCount));
            encoded.CopyTo(bytes, byteIndex);
            return encoded.Length;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            Latin1.GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override int GetMaxByteCount(int charCount) => (charCount + 1) * 4;

        public override int GetMaxCharCount(int byteCount) => byteCount;
    }

    /// <summary>Juegos de caracteres habituales en NAS con clientes Windows/DOS en español.</summary>
    private static readonly Encoding[] ServerCharsets = LoadCharsets();

    private static Encoding[] LoadCharsets()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return [Encoding.GetEncoding(1252), Encoding.Latin1, Encoding.GetEncoding(850), Encoding.GetEncoding(437)];
    }

    private static readonly RawEncoding Exact = new("bytes tal cual", s => Encoding.Latin1.GetBytes(s));

    /// <summary>
    /// Formas de enviar una ruta, en orden de prueba. "UTF-8 → X": el servidor convierte de UTF-8 a X lo que recibe, así
    /// que se envían los bytes leídos como X. "X → UTF-8": el servidor convierte de X a UTF-8, así que se envía el
    /// nombre UTF-8 codificado en X.
    /// </summary>
    private static readonly RawEncoding[] Modes =
    [
        Exact,
        .. ServerCharsets.Select(cs => new RawEncoding($"UTF-8 → {cs.WebName}", s => Encoding.UTF8.GetBytes(cs.GetString(Encoding.Latin1.GetBytes(s))))),
        .. ServerCharsets.Select(cs => new RawEncoding($"{cs.WebName} → UTF-8", s =>
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            return Utf8.IsValid(bytes) ? cs.GetBytes(Encoding.UTF8.GetString(bytes)) : bytes;
        })),
    ];

    /// <summary>
    /// Sesión FTP. Con <paramref name="raw"/>, el servidor habla en bytes (<see cref="RawEncoding"/>) y la sesión
    /// traduce: hacia afuera muestra nombres legibles (UTF-8 si lo son, si no Latin-1) y recuerda los bytes
    /// originales de cada ruta listada para pedirla después tal como la tiene el servidor.
    /// </summary>
    private sealed class FtpSession : IFileSession
    {
        private readonly AsyncFtpClient _client;
        private readonly string _home;
        private readonly bool _raw;
        private readonly bool _preferLatin1;

        /// <summary>Avisos para la bitácora (p. ej. qué forma de envío aceptó el servidor).</summary>
        private readonly Queue<string> _notes = new();
        private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

        /// <summary>Ruta legible absoluta → ruta con los bytes del servidor.</summary>
        private readonly Dictionary<string, string> _rawPaths = new(StringComparer.Ordinal);

        /// <param name="home">Carpeta inicial del usuario; las rutas relativas se resuelven desde ella.</param>
        public FtpSession(AsyncFtpClient client, string home, bool raw, bool preferLatin1)
        {
            _client = client;
            _raw = raw;
            _preferLatin1 = preferLatin1;
            _home = DisplayName(home);
            _rawPaths[_home] = home;
        }

        public string HomeDirectory => _home;

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var status = await _client.UploadFile(localFile, ToRaw(Absolute(remotePath)), FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);
            if (status == FtpStatus.Failed)
            {
                throw new IOException($"No se pudo subir {remotePath}.");
            }
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var raw = ToRaw(Absolute(remoteDirectory));
            if (!await ExistsAsync(raw, cancellationToken))
            {
                return [];
            }

            var items = await _client.GetListing(raw, cancellationToken);
            if (!items.Any(IsEntry))
            {
                // Algunos servidores (p. ej. ProFTPD en NAS) devuelven MLSD vacío en ciertas carpetas; LIST sí las muestra.
                items = await _client.GetListing(raw, FtpListOption.ForceList, cancellationToken);
            }

            return [.. items.Where(IsEntry).Select(i =>
            {
                var name = DisplayName(i.Name);
                var path = RemotePaths.Combine(remoteDirectory, name);
                if (_raw)
                {
                    _rawPaths[Absolute(path)] = $"{raw.TrimEnd('/')}/{i.Name}";
                }

                return new RemoteEntry(
                    path,
                    name,
                    i.Type == FtpObjectType.Directory,
                    i.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc)),
                    i.Type == FtpObjectType.File ? i.Size : null);
            })];
        }

        public Task<bool> DirectoryExistsAsync(string remoteDirectory, CancellationToken cancellationToken) =>
            ExistsAsync(ToRaw(Absolute(remoteDirectory)), cancellationToken);

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            var path = Absolute(remotePath);
            var raw = ToRaw(path);
            FtpMissingObjectException? missing = null;
            var found = await TryModesAsync(raw, async () =>
            {
                try
                {
                    await DownloadExactAsync(raw, localFile, cancellationToken);
                    return true;
                }
                catch (FtpMissingObjectException ex)
                {
                    missing ??= ex;
                    return false;
                }
            });

            if (!found)
            {
                throw new FileNotFoundException(await DescribeMissingAsync(path, raw, cancellationToken), path, missing);
            }
        }

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) =>
            _client.DeleteFile(ToRaw(Absolute(remotePath)), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.Disconnect();
            }
            finally
            {
                _client.Dispose();
            }
        }

        private Task<bool> ExistsAsync(string raw, CancellationToken cancellationToken) =>
            TryModesAsync(raw, () => _client.DirectoryExists(raw, cancellationToken));

        private async Task DownloadExactAsync(string raw, string localFile, CancellationToken cancellationToken)
        {
            var status = await _client.DownloadFile(localFile, raw, FtpLocalExists.Overwrite, token: cancellationToken);
            if (status == FtpStatus.Failed)
            {
                throw new IOException($"No se pudo descargar {DisplayName(raw)}.");
            }
        }

        /// <summary>
        /// Con tildes o ñ, un "no existe" puede deberse a que el servidor convierte las rutas que recibe. Se prueba con
        /// la forma de envío actual y, si falla, con las demás (<see cref="Modes"/>), ruta por ruta: un mismo NAS puede
        /// necesitar una forma para los nombres en Latin-1 y otra para los que están en UTF-8. La que funcionó queda
        /// como primera opción para la siguiente.
        /// </summary>
        private async Task<bool> TryModesAsync(string raw, Func<Task<bool>> attempt)
        {
            if (await attempt())
            {
                return true;
            }

            if (!CanSwitchMode(raw))
            {
                return false;
            }

            var original = _client.Encoding;
            foreach (var mode in Modes.Where(m => !ReferenceEquals(m, original)))
            {
                _client.Encoding = mode;
                if (await attempt())
                {
                    if (_announced.Add(mode.Label))
                    {
                        _notes.Enqueue($"El servidor acepta nombres con tildes enviados como «{mode.Label}» ({DisplayName(raw)}).");
                    }

                    return true;
                }
            }

            _client.Encoding = original;
            return false;
        }

        private bool CanSwitchMode(string raw) => _raw && !Ascii.IsValid(raw);

        public IEnumerable<string> DrainNotes()
        {
            while (_notes.TryDequeue(out var note))
            {
                yield return note;
            }
        }

        /// <summary>Ruta con los bytes del servidor: la recordada del listado o, si no se listó, la codificada según la preferencia.</summary>
        private string ToRaw(string path)
        {
            if (!_raw)
            {
                return path;
            }

            if (_rawPaths.TryGetValue(path, out var known))
            {
                return known;
            }

            var display = string.Empty;
            var raw = string.Empty;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                display += "/" + segment;
                raw = _rawPaths.TryGetValue(display, out var prefix) ? prefix : $"{raw}/{RawSegment(segment)}";
            }

            return raw.Length == 0 ? "/" : raw;
        }

        private string RawSegment(string segment) =>
            _preferLatin1 && segment.All(c => c <= 'ÿ')
                ? segment
                : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(segment));

        /// <summary>
        /// Nombre legible a partir de los bytes del servidor: UTF-8 si es válido (y no se prefirió Latin-1); si no,
        /// Latin-1, salvo que parezca CP850 (DOS/Samba: 0xA0 = "á", 0xA1 = "í") y no tenga vocales acentuadas de Latin-1.
        /// </summary>
        private string DisplayName(string raw)
        {
            if (!_raw || _preferLatin1 || Ascii.IsValid(raw))
            {
                return raw;
            }

            var bytes = Encoding.Latin1.GetBytes(raw);
            if (Utf8.IsValid(bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            return bytes.Any(b => b is >= 0x80 and <= 0xA5) && !bytes.Any(Latin1Accents.Contains)
                ? ServerCharsets[2].GetString(bytes)
                : raw;
        }

        /// <summary>Vocales acentuadas, ñ y ü (y sus mayúsculas) en Latin-1.</summary>
        private static readonly HashSet<byte> Latin1Accents = [0xE1, 0xE9, 0xED, 0xF3, 0xFA, 0xF1, 0xFC, 0xC1, 0xC9, 0xCD, 0xD3, 0xDA, 0xD1, 0xDC];

        /// <summary>
        /// Explica un archivo que el servidor listó pero no deja descargar: cómo aparece en cada tipo de listado
        /// (MLSD, LIST, NLST), con los bytes no ASCII escapados para ver exactamente cómo está guardado el nombre.
        /// </summary>
        private async Task<string> DescribeMissingAsync(string path, string raw, CancellationToken cancellationToken)
        {
            var slash = raw.LastIndexOf('/');
            var parent = slash <= 0 ? "/" : raw[..slash];
            var name = raw[(slash + 1)..];
            var key = Skeleton(name);
            var text = new StringBuilder($"El servidor listó {path} pero responde que no existe.\nNombre pedido: {Escape(name)}");
            if (_raw)
            {
                text.Append(CanSwitchMode(raw) ? $" (probado: {string.Join(", ", Modes.Select(m => m.Label))})" : " (enviado byte a byte)");
            }

            try
            {
                foreach (var (label, option) in new[] { ("MLSD", FtpListOption.Auto), ("LIST", FtpListOption.ForceList) })
                {
                    var matches = (await _client.GetListing(parent, option, cancellationToken)).Where(i => Skeleton(i.Name) == key).ToList();
                    text.Append($"\nEn {label}: {(matches.Count == 0 ? "no aparece" : string.Empty)}");
                    foreach (var item in matches)
                    {
                        text.Append($"\n  {Escape(item.Name)} · tipo {item.Type} · línea {Escape(item.Input ?? string.Empty)}");
                    }
                }

                var names = (await _client.GetNameListing(parent, cancellationToken))
                    .Select(n => n[(n.LastIndexOf('/') + 1)..])
                    .Where(n => Skeleton(n) == key)
                    .ToList();
                text.Append($"\nEn NLST: {(names.Count == 0 ? "no aparece" : string.Join(", ", names.Select(Escape)))}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text.Append($"\n(No se pudo completar el diagnóstico: {ex.Message})");
            }

            return text.ToString();
        }

        /// <summary>Solo letras y dígitos ASCII: agrupa las variantes de un mismo nombre (tildes, espacios, codificación).</summary>
        private static string Skeleton(string name) =>
            string.Concat(name.Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant();

        /// <summary>Entre «» y con \uXXXX para todo lo que no sea ASCII imprimible (deja ver espacios de más o NFD).</summary>
        private static string Escape(string value) =>
            "«" + string.Concat(value.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}")) + "»";

        private static bool IsEntry(FtpListItem item) => item.Type is FtpObjectType.File or FtpObjectType.Directory;

        /// <summary>Rutas con '/' inicial son absolutas; el resto se resuelve desde la carpeta inicial.</summary>
        private string Absolute(string path) => RemotePaths.Resolve(_home, path);
    }
}

public sealed class FtpSource() : FileSessionSource(new FtpSessionFactory());

public sealed class FtpDestination() : FileSessionDestination(new FtpSessionFactory());
