using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.Dropbox;

/// <summary>Dropbox (API v2) con app key, app secret y refresh token de acceso sin conexión.</summary>
internal sealed class DropboxSessionFactory : IFileSessionFactory
{
    private const string Api = "https://api.dropboxapi.com/2";
    private const string Content = "https://content.dropboxapi.com/2";

    /// <summary>Hasta 150 MB se sube de una vez; más grande, por sesión en trozos.</summary>
    private const long SimpleUploadLimit = 128L * 1024 * 1024;

    private const int ChunkSize = 32 * 1024 * 1024;

    public static readonly OAuthSpec OAuth = new("Dropbox", "Dropbox", string.Empty, "appKey", "appSecret", "refreshToken")
    {
        AuthorizeUrl = _ => "https://www.dropbox.com/oauth2/authorize",
        TokenUrl = _ => "https://api.dropboxapi.com/oauth2/token",
        ExtraParameters = new Dictionary<string, string> { ["token_access_type"] = "offline" },
        SetupHelp = "dropbox.com/developers/apps → tu app → Settings → OAuth 2 → Redirect URIs: agrega este URI.",
    };

    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "dropbox",
        "Dropbox",
        role == ProviderRole.Source
            ? "Descarga una carpeta de Dropbox y la empaqueta en .zip."
            : "Carpeta de Dropbox (personal o de equipo).",
        ProviderCategory.CloudDrive,
        "cloud",
        [
            SettingField.Text("appKey", "App key",
                help: "dropbox.com/developers/apps → Create app (Scoped access). Permisos: files.metadata.read, files.content.read y files.content.write.").ForConnection(),
            SettingField.Secret("appSecret", "App secret", help: "App key y secret vacíos = la app configurada por el administrador (Backup__OAuth__Dropbox__ClientId).").ForConnection(),
            SettingField.Secret("refreshToken", "Refresh token", required: true,
                help: "Se llena solo con «Conectar con Dropbox». No caduca.").ForConnection(),
            SettingField.Text("remotePath", "Carpeta", placeholder: "/Respaldos").Browsable(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        KeyValuePair<string, string>[] form =
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", settings.GetRaw("refreshToken") ?? throw new InvalidOperationException("Falta el refresh token.")),
            new("client_id", settings.Require("appKey")),
            new("client_secret", settings.GetRaw("appSecret") ?? throw new InvalidOperationException("Falta el app secret.")),
        ];
        var token = new AccessToken(ct => CloudHttp.TokenAsync("https://api.dropboxapi.com/oauth2/token", form, ct));
        await token.GetAsync(cancellationToken);
        return new DropboxSession(token);
    }

    private sealed class DropboxSession(AccessToken token) : IFileSession
    {
        private readonly HttpClient _http = CloudHttp.Shared;

        /// <summary>Dropbox usa "" para la raíz y "/a/b" para lo demás.</summary>
        private static string ApiPath(string path) => CloudHttp.Trim(path) is { Length: > 0 } clean ? "/" + clean : string.Empty;

        /// <summary>
        /// Argumentos para la cabecera Dropbox-API-Arg: JSON con los caracteres no ASCII escapados (\uXXXX), que es
        /// lo que exige una cabecera HTTP. El serializador por defecto ya los escapa.
        /// </summary>
        private static string Arg(object value) => JsonSerializer.Serialize(value);

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var entries = new List<RemoteEntry>();
            var response = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Api}/files/list_folder")
            {
                Content = CloudHttp.Json(new { path = ApiPath(remoteDirectory), limit = 2000 }),
            }, cancellationToken, accept: r => r.StatusCode == HttpStatusCode.Conflict);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // 409 = error de la ruta (no existe, no es carpeta...).
                var error = await CloudHttp.ErrorAsync(response, cancellationToken);
                return error.Message.Contains("not_found", StringComparison.Ordinal) ? entries : throw error;
            }

            var page = await CloudHttp.ReadJsonAsync(response, cancellationToken);
            while (true)
            {
                foreach (var item in page["entries"]?.AsArray() ?? [])
                {
                    var tag = item?[".tag"]?.GetValue<string>();
                    if (item is null || tag is not ("file" or "folder"))
                    {
                        continue;
                    }

                    var name = item["name"]!.GetValue<string>();
                    entries.Add(new RemoteEntry(
                        RemotePaths.Combine(remoteDirectory, name),
                        name,
                        tag == "folder",
                        DateTimeOffset.TryParse(item["server_modified"]?.GetValue<string>(), out var modified) ? modified : null,
                        tag == "file" ? item["size"]?.GetValue<long>() : null));
                }

                if (page["has_more"]?.GetValue<bool>() != true)
                {
                    return entries;
                }

                var cursor = page["cursor"]!.GetValue<string>();
                page = await CloudHttp.ReadJsonAsync(await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Api}/files/list_folder/continue")
                {
                    Content = CloudHttp.Json(new { cursor }),
                }, cancellationToken), cancellationToken);
            }
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            var response = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Content}/files/download")
            {
                Headers = { { "Dropbox-API-Arg", Arg(new { path = ApiPath(remotePath) }) } },
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead, accept: r => r.StatusCode == HttpStatusCode.Conflict);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var error = await CloudHttp.ErrorAsync(response, cancellationToken);
                throw error.Message.Contains("not_found", StringComparison.Ordinal) ? new FileNotFoundException(error.Message) : error;
            }

            await CloudHttp.SaveAsync(response, localFile, cancellationToken);
        }

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var commit = new { path = ApiPath(remotePath), mode = "overwrite", mute = true };
            var length = new FileInfo(localFile).Length;
            if (length <= SimpleUploadLimit)
            {
                using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Content}/files/upload")
                {
                    Headers = { { "Dropbox-API-Arg", Arg(commit) } },
                    Content = Octets(new StreamContent(File.OpenRead(localFile), 1 << 20)),
                }, cancellationToken);
                return;
            }

            await using var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var buffer = new byte[ChunkSize];
            var first = await input.ReadAtLeastAsync(buffer, ChunkSize, throwOnEndOfStream: true, cancellationToken);
            var started = await CloudHttp.ReadJsonAsync(await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Content}/files/upload_session/start")
            {
                Headers = { { "Dropbox-API-Arg", Arg(new { close = false }) } },
                Content = Octets(new ByteArrayContent(buffer, 0, first)),
            }, cancellationToken), cancellationToken);
            var sessionId = started["session_id"]!.GetValue<string>();
            long offset = first;

            while (true)
            {
                var read = await input.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, length - offset), throwOnEndOfStream: false, cancellationToken);
                var cursor = new { session_id = sessionId, offset };
                if (offset + read >= length)
                {
                    using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Content}/files/upload_session/finish")
                    {
                        Headers = { { "Dropbox-API-Arg", Arg(new { cursor, commit }) } },
                        Content = Octets(new ByteArrayContent(buffer, 0, read)),
                    }, cancellationToken);
                    return;
                }

                using (await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Content}/files/upload_session/append_v2")
                {
                    Headers = { { "Dropbox-API-Arg", Arg(new { cursor, close = false }) } },
                    Content = Octets(new ByteArrayContent(buffer, 0, read)),
                }, cancellationToken))
                {
                    offset += read;
                }
            }
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
        {
            using var _ = await token.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, $"{Api}/files/delete_v2")
            {
                Content = CloudHttp.Json(new { path = ApiPath(remotePath) }),
            }, cancellationToken, accept: r => r.StatusCode == HttpStatusCode.Conflict); // 409 = ya no existe.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static HttpContent Octets(HttpContent content)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return content;
        }
    }
}

public sealed class DropboxSource() : FileSessionSource(new DropboxSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => DropboxSessionFactory.OAuth;
}

public sealed class DropboxDestination() : FileSessionDestination(new DropboxSessionFactory()), IOAuthConnectable
{
    public OAuthSpec OAuth => DropboxSessionFactory.OAuth;
}
