using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.OneDrive;

/// <summary>
/// OneDrive y bibliotecas de SharePoint con Microsoft Graph. Con una aplicación de Entra ID (client credentials) accede
/// al OneDrive de cualquier usuario o a un sitio de SharePoint; con refresh token, al OneDrive de quien lo autorizó.
/// </summary>
internal sealed class OneDriveSessionFactory : IFileSessionFactory
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>Graph pide trozos múltiplos de 320 KiB; 10 MiB equilibra memoria y número de peticiones.</summary>
    private const int ChunkSize = 320 * 1024 * 32;

    private const long SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>Autorización delegada (OneDrive personal o de un usuario concreto); el tenant decide qué cuentas valen.</summary>
    public static readonly OAuthSpec OAuth = new("Microsoft", "Microsoft", "openid email Files.ReadWrite.All offline_access", "clientId", "clientSecret", "refreshToken")
    {
        AuthorizeUrl = s => $"https://login.microsoftonline.com/{Uri.EscapeDataString(s.Get("tenantId") ?? "common")}/oauth2/v2.0/authorize",
        TokenUrl = s => $"https://login.microsoftonline.com/{Uri.EscapeDataString(s.Get("tenantId") ?? "common")}/oauth2/v2.0/token",
        ExtraParameters = new Dictionary<string, string> { ["prompt"] = "select_account" },
        SetupHelp = "Entra ID → Registros de aplicaciones: agrega la plataforma «Web» con este URI de redireccionamiento y el permiso delegado Files.ReadWrite.All de Microsoft Graph.",
    };

    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "onedrive",
        "OneDrive / SharePoint",
        role == ProviderRole.Source
            ? "Descarga una carpeta de OneDrive o de una biblioteca de SharePoint y la empaqueta en .zip."
            : "Carpeta de OneDrive o de una biblioteca de documentos de SharePoint.",
        ProviderCategory.CloudDrive,
        "cloud",
        [
            SettingField.Text("tenantId", "ID del inquilino (tenant)", placeholder: "empresa.onmicrosoft.com o GUID",
                help: "Entra ID → Información general. Para OneDrive personal (cuenta Microsoft) escribe «consumers».").ForConnection(),
            SettingField.Text("clientId", "ID de la aplicación (client)",
                help: "Entra ID → Registros de aplicaciones. Permisos de aplicación de Graph: Files.ReadWrite.All (y Sites.ReadWrite.All para SharePoint), con consentimiento del administrador.").ForConnection(),
            SettingField.Secret("clientSecret", "Secreto de la aplicación",
                help: "Certificados y secretos → Nuevo secreto. Anota su vencimiento: al caducar fallarán los respaldos.").ForConnection(),
            SettingField.Secret("refreshToken", "Refresh token (opcional)",
                help: "Solo para OneDrive personal o permisos delegados: se llena con «Conectar con Microsoft». Vacío = permisos de aplicación.").ForConnection(),
            SettingField.Text("drive", "Unidad", placeholder: "usuario@empresa.com o https://empresa.sharepoint.com/sites/Finanzas",
                help: "Correo del usuario (su OneDrive), URL de un sitio de SharePoint (su biblioteca Documentos) o ID de unidad (b!...). Vacío = la unidad de quien autorizó el refresh token.").ForConnection(),
            SettingField.Text("remotePath", "Carpeta", placeholder: "/Respaldos").Browsable(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var refreshToken = settings.GetRaw("refreshToken");
        var tenant = settings.Get("tenantId") ?? (refreshToken is null ? throw new InvalidOperationException("Indica el ID del inquilino.") : "common");
        var endpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token";
        var form = new List<KeyValuePair<string, string>> { new("client_id", settings.Require("clientId")) };
        if (settings.GetRaw("clientSecret") is { } secret)
        {
            form.Add(new("client_secret", secret));
        }

        if (refreshToken is not null)
        {
            form.AddRange([new("grant_type", "refresh_token"), new("refresh_token", refreshToken), new("scope", "Files.ReadWrite.All offline_access")]);
        }
        else
        {
            if (settings.GetRaw("clientSecret") is null)
            {
                throw new InvalidOperationException("Indica el secreto de la aplicación (o un refresh token).");
            }

            form.AddRange([new("grant_type", "client_credentials"), new("scope", "https://graph.microsoft.com/.default")]);
        }

        var token = new AccessToken(ct => CloudHttp.TokenAsync(endpoint, form, ct));
        var drive = await ResolveDriveAsync(token, settings.Get("drive"), refreshToken is not null, cancellationToken);
        return new GraphSession(token, drive);
    }

    /// <summary>Base de la unidad en Graph: /users/{upn}/drive, /sites/{id}/drive, /drives/{id} o /me/drive.</summary>
    private static async Task<string> ResolveDriveAsync(AccessToken token, string? drive, bool delegated, CancellationToken cancellationToken)
    {
        if (drive is null)
        {
            return delegated
                ? Graph + "/me/drive"
                : throw new InvalidOperationException("Con permisos de aplicación indica la unidad: el correo del usuario o la URL del sitio de SharePoint.");
        }

        if (drive.Contains('@', StringComparison.Ordinal))
        {
            return $"{Graph}/users/{Uri.EscapeDataString(drive)}/drive";
        }

        if (Uri.TryCreate(drive, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
        {
            var sitePath = url.AbsolutePath.TrimEnd('/');
            var site = await CloudHttp.ReadJsonAsync(await token.SendAsync(CloudHttp.Shared,
                () => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/sites/{url.Host}:{(sitePath.Length == 0 ? "/" : sitePath)}?$select=id"),
                cancellationToken), cancellationToken);
            return $"{Graph}/sites/{site["id"]!.GetValue<string>()}/drive";
        }

        return $"{Graph}/drives/{Uri.EscapeDataString(drive)}";
    }

    private sealed class GraphSession(AccessToken token, string drive) : IFileSession
    {
        private readonly HttpClient _http = CloudHttp.Shared;

        /// <summary>Elemento por ruta: "root" o "root:/a/b:" (cada segmento codificado).</summary>
        private string Item(string path)
        {
            var encoded = CloudHttp.EncodePath(path);
            return encoded.Length == 0 ? $"{drive}/root" : $"{drive}/root:/{encoded}:";
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var entries = new List<RemoteEntry>();
            string? url = $"{Item(remoteDirectory)}/children?$top=1000&$select=name,size,lastModifiedDateTime,file,folder";
            while (url is not null)
            {
                var next = url;
                var response = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, next), cancellationToken, allowNotFound: true);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    return entries;
                }

                var page = await CloudHttp.ReadJsonAsync(response, cancellationToken);
                foreach (var item in page["value"]?.AsArray() ?? [])
                {
                    if (item is null || (item["file"] is null && item["folder"] is null))
                    {
                        continue; // Paquetes (p. ej. blocs de OneNote) y elementos remotos: no son archivos descargables.
                    }

                    var name = item["name"]!.GetValue<string>();
                    entries.Add(new RemoteEntry(
                        RemotePaths.Combine(remoteDirectory, name),
                        name,
                        item["folder"] is not null,
                        DateTimeOffset.TryParse(item["lastModifiedDateTime"]?.GetValue<string>(), out var modified) ? modified : null,
                        item["file"] is not null ? item["size"]?.GetValue<long>() : null));
                }

                url = page["@odata.nextLink"]?.GetValue<string>();
            }

            return entries;
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            // Graph responde con una redirección a una URL ya firmada; HttpClient la sigue (sin la cabecera de autorización).
            var response = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, $"{Item(remotePath)}/content"),
                cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            await CloudHttp.SaveAsync(response, localFile, cancellationToken);
        }

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var length = new FileInfo(localFile).Length;
            if (length <= SimpleUploadLimit)
            {
                // Graph crea las carpetas intermedias que falten.
                using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Put, $"{Item(remotePath)}/content")
                {
                    Content = new StreamContent(File.OpenRead(localFile)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
                }, cancellationToken);
                return;
            }

            var session = await CloudHttp.ReadJsonAsync(await token.SendAsync(_http, () =>
                new HttpRequestMessage(HttpMethod.Post, $"{Item(remotePath)}/createUploadSession")
                {
                    Content = CloudHttp.Json(new Dictionary<string, object>
                    {
                        ["item"] = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "replace" },
                    }),
                }, cancellationToken), cancellationToken);
            var uploadUrl = session["uploadUrl"]!.GetValue<string>();

            await using var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            while (offset < length)
            {
                var read = await input.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, length - offset), throwOnEndOfStream: true, cancellationToken);
                var start = offset;
                // La URL de la sesión ya está autorizada: no lleva token.
                using var _ = await CloudHttp.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(buffer, 0, read)
                    {
                        Headers = { ContentRange = new ContentRangeHeaderValue(start, start + read - 1, length) },
                    },
                }, cancellationToken);
                offset += read;
            }
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
        {
            using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Delete, Item(remotePath)), cancellationToken, allowNotFound: true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class OneDriveSource() : FileSessionSource(new OneDriveSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => OneDriveSessionFactory.OAuth;
}

public sealed class OneDriveDestination() : FileSessionDestination(new OneDriveSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => OneDriveSessionFactory.OAuth;
}
