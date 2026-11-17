using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.GoogleDrive;

/// <summary>
/// Google Drive (API v3) con cuenta de servicio o con OAuth (refresh token). Drive identifica todo por ID, así que las
/// rutas "/a/b" se resuelven carpeta por carpeta y se recuerdan durante la sesión.
/// </summary>
internal sealed class GoogleDriveSessionFactory : IFileSessionFactory
{
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string Scope = "https://www.googleapis.com/auth/drive";

    /// <summary>Documentos nativos de Google: no tienen archivo, se exportan al formato de Office equivalente.</summary>
    private static readonly Dictionary<string, (string Mime, string Extension)> Exports = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        ["application/vnd.google-apps.drawing"] = ("application/pdf", ".pdf"),
    };

    public static readonly OAuthSpec OAuth = new("Google", "Google", "openid email " + Scope, "clientId", "clientSecret", "refreshToken")
    {
        AuthorizeUrl = _ => "https://accounts.google.com/o/oauth2/v2/auth",
        TokenUrl = _ => "https://oauth2.googleapis.com/token",
        // offline + consent: Google solo entrega el refresh token así (y de nuevo en cada autorización).
        ExtraParameters = new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "true" },
        AppliesTo = s => s.Get("authMode", "ServiceAccount") == "OAuth",
        SetupHelp = "Google Cloud Console → APIs y servicios: habilita la Google Drive API, publica la pantalla de consentimiento en «Producción» y crea un ID de cliente de OAuth de tipo «Aplicación web» con este URI de redireccionamiento.",
    };

    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "googledrive",
        "Google Drive",
        role == ProviderRole.Source
            ? "Descarga una carpeta de Google Drive (Docs, Sheets y Slides se exportan a Office) y la empaqueta en .zip."
            : "Carpeta de Google Drive o de una unidad compartida.",
        ProviderCategory.CloudDrive,
        "cloud",
        [
            SettingField.Select("authMode", "Autenticación", ["OAuth", "ServiceAccount"], "OAuth",
                "OAuth: autoriza una cuenta (Gmail o Workspace) con el botón «Conectar con Google». ServiceAccount: cuenta de servicio de Google Workspace, con una unidad compartida o suplantando a un usuario.").ForConnection(),
            SettingField.Secret("serviceAccountJson", "JSON de la cuenta de servicio",
                help: "Google Cloud → IAM → Cuentas de servicio → Claves → Agregar clave (JSON). Pega el contenido completo del archivo. Habilita la API de Google Drive en el proyecto.").ForConnection(),
            SettingField.Text("impersonate", "Suplantar a (opcional)", placeholder: "respaldos@empresa.com",
                help: "Con delegación de todo el dominio: trabaja en el Drive de ese usuario. Sin esto, la cuenta de servicio no tiene espacio propio y solo puede escribir en unidades compartidas.").ForConnection(),
            SettingField.Text("clientId", "Client ID (OAuth)", placeholder: "1234-abc.apps.googleusercontent.com",
                help: "Vacío = el cliente configurado por el administrador (Backup__OAuth__Google__ClientId).").ForConnection(),
            SettingField.Secret("clientSecret", "Client secret (OAuth)").ForConnection(),
            SettingField.Secret("refreshToken", "Refresh token (OAuth)",
                help: "Se llena solo con «Conectar con Google». Publica la app OAuth en «Producción»: en modo de prueba el token caduca a los 7 días.").ForConnection(),
            SettingField.Text("sharedDriveId", "ID de la unidad compartida (opcional)", placeholder: "0ABcDeFgHiJkLmNoPQ",
                help: "Lo que sigue a /drive/folders/ al abrir la unidad compartida. Vacío = Mi unidad.").ForConnection(),
            SettingField.Text("remotePath", "Carpeta", placeholder: "/Respaldos").Browsable(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var token = new AccessToken(settings.Get("authMode", "ServiceAccount") == "OAuth"
            ? ct => CloudHttp.TokenAsync("https://oauth2.googleapis.com/token",
            [
                new("grant_type", "refresh_token"),
                new("client_id", settings.Require("clientId")),
                new("client_secret", settings.GetRaw("clientSecret") ?? string.Empty),
                new("refresh_token", settings.GetRaw("refreshToken") ?? throw new InvalidOperationException("Falta el refresh token.")),
            ], ct)
            : ct => ServiceAccountTokenAsync(settings, ct));

        await token.GetAsync(cancellationToken); // Falla aquí, con un mensaje claro, si las credenciales no sirven.
        return new DriveSession(token, settings.Get("sharedDriveId"));
    }

    private static Task<(string, TimeSpan)> ServiceAccountTokenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var raw = settings.GetRaw("serviceAccountJson") ?? throw new InvalidOperationException("Falta el JSON de la cuenta de servicio.");
        JsonNode key;
        try
        {
            key = JsonNode.Parse(raw) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("El JSON de la cuenta de servicio no es válido: pega el contenido completo del archivo .json.");
        }

        var email = key["client_email"]?.GetValue<string>() ?? throw new InvalidOperationException("El JSON no tiene client_email.");
        var pem = key["private_key"]?.GetValue<string>() ?? throw new InvalidOperationException("El JSON no tiene private_key.");
        var tokenUri = key["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> { ["iss"] = email, ["scope"] = Scope, ["aud"] = tokenUri, ["iat"] = now, ["exp"] = now + 3600 };
        if (settings.Get("impersonate") is { } subject)
        {
            claims["sub"] = subject;
        }

        var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }))
                       + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return CloudHttp.TokenAsync(tokenUri,
        [
            new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new("assertion", unsigned + "." + Base64Url(signature)),
        ], cancellationToken);
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record DriveItem(string Id, string Name, string MimeType, long? Size, DateTimeOffset? Modified)
    {
        public bool IsFolder => MimeType == FolderMime;
    }

    private sealed class DriveSession(AccessToken token, string? sharedDriveId) : IFileSession
    {
        private readonly HttpClient _http = CloudHttp.Shared;
        private readonly string _rootId = sharedDriveId ?? "root";

        /// <summary>Ruta ("a/b" o "a/b/archivo.docx", sin '/' inicial) → elemento de Drive, para no resolverla dos veces.</summary>
        private readonly Dictionary<string, DriveItem> _items = new(StringComparer.Ordinal);

        private readonly List<string> _notes = [];

        public IEnumerable<string> DrainNotes()
        {
            var notes = _notes.ToArray();
            _notes.Clear();
            return notes;
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var directory = CloudHttp.Trim(remoteDirectory);
            if (await ResolveFolderAsync(directory, create: false, cancellationToken) is not { } folderId)
            {
                return [];
            }

            var entries = new List<RemoteEntry>();
            await foreach (var item in ChildrenAsync(folderId, null, cancellationToken))
            {
                var name = item.Name;
                if (!item.IsFolder && item.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
                {
                    if (!Exports.TryGetValue(item.MimeType, out var export))
                    {
                        continue; // Formularios, accesos directos, Sites...: no tienen contenido descargable.
                    }

                    name += export.Extension;
                }

                var path = directory.Length == 0 ? name : directory + "/" + name;
                _items[path] = item;
                entries.Add(new RemoteEntry(RemotePaths.Combine(remoteDirectory, name), name, item.IsFolder, item.Modified, item.Size));
            }

            return entries;
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            var item = await FindAsync(remotePath, cancellationToken) ?? throw new FileNotFoundException($"No existe en Google Drive: {remotePath}");
            var url = Exports.TryGetValue(item.MimeType, out var export)
                ? $"{Api}/{item.Id}/export?mimeType={Uri.EscapeDataString(export.Mime)}"
                : $"{Api}/{item.Id}?alt=media&supportsAllDrives=true&acknowledgeAbuse=true";
            var response = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken,
                HttpCompletionOption.ResponseHeadersRead);
            await CloudHttp.SaveAsync(response, localFile, cancellationToken);
            if (export.Extension is not null)
            {
                _notes.Add($"Exportado de Google a {export.Extension}: {remotePath}");
            }
        }

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var (directory, name) = CloudHttp.Split(remotePath);
            var parentId = await ResolveFolderAsync(directory, create: true, cancellationToken)
                           ?? throw new InvalidOperationException($"No se pudo crear la carpeta {directory}.");

            // Si ya existe se reemplaza su contenido: Drive admite nombres repetidos y quedarían duplicados.
            var existing = await ChildrenAsync(parentId, name, cancellationToken).FirstOrDefaultAsync(i => !i.IsFolder, cancellationToken);
            using var session = await token.SendAsync(_http, () => existing is null
                ? new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}?uploadType=resumable&supportsAllDrives=true")
                {
                    Content = CloudHttp.Json(new { name, parents = new[] { parentId } }),
                }
                : new HttpRequestMessage(HttpMethod.Patch, $"{UploadApi}/{existing.Id}?uploadType=resumable&supportsAllDrives=true")
                {
                    Content = CloudHttp.Json(new { }),
                }, cancellationToken);
            var location = session.Headers.Location ?? throw new InvalidDataException("Google Drive no devolvió la URL de subida.");

            using var _ = await CloudHttp.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Put, location)
            {
                Content = new StreamContent(File.OpenRead(localFile), 1 << 20)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                },
            }, cancellationToken);
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
        {
            if (await FindAsync(remotePath, cancellationToken) is { } item)
            {
                using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Delete, $"{Api}/{item.Id}?supportsAllDrives=true"),
                    cancellationToken, allowNotFound: true);
                _items.Remove(CloudHttp.Trim(remotePath));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<DriveItem?> FindAsync(string remotePath, CancellationToken cancellationToken)
        {
            var path = CloudHttp.Trim(remotePath);
            if (_items.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var (directory, _) = CloudHttp.Split(path);
            await ListAsync(directory, cancellationToken); // Llena la caché con los nombres tal como se muestran.
            return _items.GetValueOrDefault(path);
        }

        /// <summary>ID de la carpeta "a/b" ("" = raíz); null si no existe y no se pidió crearla.</summary>
        private async Task<string?> ResolveFolderAsync(string directory, bool create, CancellationToken cancellationToken)
        {
            var currentId = _rootId;
            var currentPath = string.Empty;
            foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = currentPath.Length == 0 ? part : currentPath + "/" + part;
                if (_items.TryGetValue(currentPath, out var known) && known.IsFolder)
                {
                    currentId = known.Id;
                    continue;
                }

                var folder = await ChildrenAsync(currentId, part, cancellationToken).FirstOrDefaultAsync(i => i.IsFolder, cancellationToken);
                if (folder is null)
                {
                    if (!create)
                    {
                        return null;
                    }

                    var parentId = currentId;
                    var created = await CloudHttp.ReadJsonAsync(await token.SendAsync(_http, () =>
                        new HttpRequestMessage(HttpMethod.Post, $"{Api}?supportsAllDrives=true&fields=id")
                        {
                            Content = CloudHttp.Json(new { name = part, mimeType = FolderMime, parents = new[] { parentId } }),
                        }, cancellationToken), cancellationToken);
                    folder = new DriveItem(created["id"]!.GetValue<string>(), part, FolderMime, null, null);
                }

                _items[currentPath] = folder;
                currentId = folder.Id;
            }

            return currentId;
        }

        /// <summary>Hijos de una carpeta (todos, o solo los que se llaman <paramref name="name"/>), sin los de la papelera.</summary>
        private async IAsyncEnumerable<DriveItem> ChildrenAsync(
            string folderId, string? name, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var query = $"'{Escape(folderId)}' in parents and trashed = false" + (name is null ? string.Empty : $" and name = '{Escape(name)}'");
            var scope = sharedDriveId is null
                ? "&corpora=user"
                : $"&corpora=drive&driveId={Uri.EscapeDataString(sharedDriveId)}";
            string? pageToken = null;
            do
            {
                var url = $"{Api}?q={Uri.EscapeDataString(query)}&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true{scope}"
                          + "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)"
                          + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
                var page = await CloudHttp.ReadJsonAsync(
                    await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken), cancellationToken);
                foreach (var file in page["files"]?.AsArray() ?? [])
                {
                    if (file is null)
                    {
                        continue;
                    }

                    yield return new DriveItem(
                        file["id"]!.GetValue<string>(),
                        file["name"]!.GetValue<string>(),
                        file["mimeType"]?.GetValue<string>() ?? "application/octet-stream",
                        long.TryParse(file["size"]?.GetValue<string>(), out var size) ? size : null,
                        DateTimeOffset.TryParse(file["modifiedTime"]?.GetValue<string>(), out var modified) ? modified : null);
                }

                pageToken = page["nextPageToken"]?.GetValue<string>();
            }
            while (pageToken is not null);
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }
}

public sealed class GoogleDriveSource() : FileSessionSource(new GoogleDriveSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => GoogleDriveSessionFactory.OAuth;
}

public sealed class GoogleDriveDestination() : FileSessionDestination(new GoogleDriveSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => GoogleDriveSessionFactory.OAuth;
}
