using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.WebDav;

/// <summary>WebDAV: Nextcloud, ownCloud, NAS Synology/QNAP, Apache/Nginx con módulo DAV...</summary>
internal sealed class WebDavSessionFactory : IFileSessionFactory
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");

    private const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>
        """;

    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "webdav",
        "WebDAV / Nextcloud",
        role == ProviderRole.Source
            ? "Descarga una carpeta WebDAV (Nextcloud, ownCloud, NAS) y la empaqueta en .zip."
            : "Nextcloud, ownCloud, NAS o cualquier servidor WebDAV.",
        ProviderCategory.FileTransfer,
        "share",
        [
            SettingField.Text("url", "URL WebDAV", required: true, placeholder: "https://nube.empresa.com/remote.php/dav/files/usuario/",
                help: "Nextcloud/ownCloud: Archivos → Configuración de archivos → WebDAV.").ForConnection(),
            SettingField.Text("user", "Usuario").ForConnection(),
            SettingField.Secret("password", "Contraseña", help: "En Nextcloud, mejor una contraseña de aplicación (Seguridad → Dispositivos y sesiones).").ForConnection(),
            SettingField.Text("remotePath", "Carpeta", placeholder: "/Respaldos").Browsable(),
            SettingField.Toggle("acceptAnyCertificate", "Aceptar cualquier certificado", false, "Útil con certificados autofirmados.").ForConnection(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.Require("url");
        if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("La URL WebDAV debe empezar con https:// (o http://).");
        }

        var acceptAny = settings.GetBool("acceptAnyCertificate");
        var http = acceptAny ? CloudHttp.Create(acceptAnyCertificate: true) : CloudHttp.Shared;
        AuthenticationHeaderValue? auth = settings.Get("user") is { } user
            ? new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{settings.GetRaw("password")}")))
            : null;
        return Task.FromResult<IFileSession>(new WebDavSession(http, ownsClient: acceptAny, baseUri, auth));
    }

    /// <summary>Respuesta multistatus de PROPFIND: cada elemento menos la propia carpeta.</summary>
    internal static IEnumerable<(string Name, bool IsDirectory, DateTimeOffset? Modified, long? Size)> Parse(XDocument xml, Uri folderUri)
    {
        var self = Uri.UnescapeDataString(folderUri.AbsolutePath).TrimEnd('/');
        foreach (var response in xml.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            // href puede ser una ruta ("/remote.php/...") o una URL completa.
            var path = Uri.UnescapeDataString(Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal)
                ? absolute.AbsolutePath
                : href).TrimEnd('/');
            if (string.Equals(path, self, StringComparison.Ordinal))
            {
                continue;
            }

            var prop = response.Descendants(Dav + "prop").FirstOrDefault();
            var isDirectory = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            var name = path[(path.LastIndexOf('/') + 1)..];
            yield return (
                name,
                isDirectory,
                DateTimeOffset.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, out var modified) ? modified : null,
                !isDirectory && long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var size) ? size : null);
        }
    }

    private sealed class WebDavSession(HttpClient http, bool ownsClient, Uri baseUri, AuthenticationHeaderValue? auth) : IFileSession
    {
        private readonly HashSet<string> _knownFolders = new(StringComparer.Ordinal);

        private Uri UriFor(string path, bool folder = false)
        {
            var encoded = CloudHttp.EncodePath(path);
            return new Uri(baseUri, encoded.Length == 0 ? string.Empty : encoded + (folder ? "/" : string.Empty));
        }

        private HttpRequestMessage Request(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = auth;
            return request;
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var folderUri = UriFor(remoteDirectory, folder: true);
            using var response = await CloudHttp.SendAsync(http, () =>
            {
                var request = Request(PropFind, folderUri);
                request.Headers.Add("Depth", "1");
                request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
                return request;
            }, cancellationToken, allowNotFound: true);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return [.. WebDavSessionFactory.Parse(xml, folderUri)
                .Select(e => new RemoteEntry(RemotePaths.Combine(remoteDirectory, e.Name), e.Name, e.IsDirectory, e.Modified, e.Size))];
        }

        public async Task<bool> DirectoryExistsAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var uri = UriFor(remoteDirectory, folder: true);
            using var response = await CloudHttp.SendAsync(http, () =>
            {
                var request = Request(PropFind, uri);
                request.Headers.Add("Depth", "0");
                return request;
            }, cancellationToken, allowNotFound: true);
            return response.IsSuccessStatusCode;
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            var uri = UriFor(remotePath);
            var response = await CloudHttp.SendAsync(http, () => Request(HttpMethod.Get, uri), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            await CloudHttp.SaveAsync(response, localFile, cancellationToken);
        }

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var (directory, _) = CloudHttp.Split(remotePath);
            await EnsureFolderAsync(directory, cancellationToken);
            var uri = UriFor(remotePath);
            using var _ = await CloudHttp.SendAsync(http, () =>
            {
                var request = Request(HttpMethod.Put, uri);
                request.Content = new StreamContent(File.OpenRead(localFile), 1 << 20)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                };
                return request;
            }, cancellationToken);
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
        {
            var uri = UriFor(remotePath);
            using var _ = await CloudHttp.SendAsync(http, () => Request(HttpMethod.Delete, uri), cancellationToken, allowNotFound: true);
        }

        /// <summary>MKCOL de cada nivel; 405 = ya existe.</summary>
        private async Task EnsureFolderAsync(string directory, CancellationToken cancellationToken)
        {
            var current = string.Empty;
            foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                if (!_knownFolders.Add(current))
                {
                    continue;
                }

                var uri = UriFor(current, folder: true);
                using var _ = await CloudHttp.SendAsync(http, () => Request(MkCol, uri), cancellationToken,
                    accept: r => r.StatusCode == HttpStatusCode.MethodNotAllowed);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (ownsClient)
            {
                http.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}

public sealed class WebDavSource() : FileSessionSource(new WebDavSessionFactory());

public sealed class WebDavDestination() : FileSessionDestination(new WebDavSessionFactory());
