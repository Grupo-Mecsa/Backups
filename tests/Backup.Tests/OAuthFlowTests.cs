using System.Net;
using System.Text;
using Backup.Application.Providers;
using Backup.Providers.Files.GoogleDrive;
using Backup.Web.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backup.Tests;

public class OAuthFlowTests
{
    private static readonly OAuthSpec Google = new GoogleDriveSource().OAuth;

    private static (OAuthFlows Flows, FakeTokenEndpoint Endpoint) Create(params (string Key, string Value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();
        var endpoint = new FakeTokenEndpoint();
        return (new OAuthFlows(configuration, endpoint, NullLogger<OAuthFlows>.Instance), endpoint);
    }

    private static ProviderSettings Settings(params (string Key, string? Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void AuthorizeUrl_UsesPkce_State_AndOfflineAccess()
    {
        var (flows, _) = Create();
        var flow = flows.Start(Google, Settings(("clientId", "mi-cliente"), ("clientSecret", "s")), "u1", "https://backup.empresa.com/oauth/callback");

        var query = QueryHelpers.ParseQuery(new Uri(flow.AuthorizeUrl).Query);
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", flow.AuthorizeUrl);
        Assert.Equal("mi-cliente", query["client_id"]);
        Assert.Equal(flow.State, query["state"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("https://backup.empresa.com/oauth/callback", query["redirect_uri"]);
        Assert.Contains("auth/drive", query["scope"].ToString());
    }

    [Fact]
    public void Start_WithoutClientId_Fails_UnlessConfiguredByAdmin()
    {
        var (flows, _) = Create();
        Assert.Throws<InvalidOperationException>(() => flows.Start(Google, Settings(), "u1", "https://x/oauth/callback"));

        var (configured, _) = Create(("Backup:OAuth:Google:ClientId", "global"), ("Backup:OAuth:Google:ClientSecret", "secreto"));
        var flow = configured.Start(Google, Settings(), "u1", "https://x/oauth/callback");
        Assert.Contains("client_id=global", flow.AuthorizeUrl);
    }

    [Fact]
    public async Task Callback_ExchangesCode_AndCopiesDefaultClient()
    {
        var (flows, endpoint) = Create(("Backup:OAuth:Google:ClientId", "global"), ("Backup:OAuth:Google:ClientSecret", "secreto"));
        var flow = flows.Start(Google, Settings(), "u1", "https://x/oauth/callback");

        var result = await flows.CompleteAsync(flow.State, "u1", "codigo-123", null, null, CancellationToken.None);

        Assert.Equal("rt-abc", result.Settings["refreshToken"]);
        Assert.Equal("global", result.Settings["clientId"]);
        Assert.Equal("secreto", result.Settings["clientSecret"]);
        Assert.Equal("ana@example.com", result.Account);
        Assert.Same(result, await flow.Result);
        Assert.Contains("code=codigo-123", endpoint.LastBody);
        Assert.Contains("code_verifier=", endpoint.LastBody);
        Assert.Contains("client_secret=secreto", endpoint.LastBody);
    }

    [Fact]
    public async Task Callback_RejectsOtherUser_AndReusedState()
    {
        var (flows, _) = Create();
        var flow = flows.Start(Google, Settings(("clientId", "c")), "u1", "https://x/oauth/callback");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flows.CompleteAsync(flow.State, "intruso", "code", null, null, CancellationToken.None));

        var result = await flows.CompleteAsync(flow.State, "u1", "code", null, null, CancellationToken.None);
        Assert.False(result.Settings.ContainsKey("clientId")); // Cliente propio: ya está en el formulario.
        await Assert.ThrowsAsync<InvalidOperationException>(() => flows.CompleteAsync(flow.State, "u1", "code", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Manual_AcceptsPastedLocalhostUrl_AndChecksState()
    {
        var (flows, endpoint) = Create();
        var flow = flows.Start(Google, Settings(("clientId", "c")), "u1", OAuthFlows.ManualRedirectUri);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flows.CompleteManuallyAsync(flow, "http://localhost/?state=otro&code=x", "u1", CancellationToken.None));

        var result = await flows.CompleteManuallyAsync(flow, $"  http://localhost/?state={flow.State}&code=4%2F0Abc&scope=x  ", "u1", CancellationToken.None);
        Assert.Equal("rt-abc", result.Settings["refreshToken"]);
        Assert.Contains("code=4%2F0Abc", endpoint.LastBody);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", endpoint.LastBody);
    }

    [Fact]
    public async Task DeniedConsent_FailsTheFlow()
    {
        var (flows, _) = Create();
        var flow = flows.Start(Google, Settings(("clientId", "c")), "u1", "https://x/oauth/callback");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flows.CompleteAsync(flow.State, "u1", null, "access_denied", null, CancellationToken.None));
        Assert.Contains("cancel", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Result);
    }

    [Theory]
    [InlineData("https://backup.empresa.com/oauth/callback", true)]
    [InlineData("http://localhost:5000/oauth/callback", true)]
    [InlineData("http://127.0.0.1:8080/oauth/callback", true)]
    [InlineData("http://192.168.1.20:8080/oauth/callback", false)]
    public void RedirectAllowed_OnlyHttpsOrLoopback(string uri, bool allowed) =>
        Assert.Equal(allowed, OAuthFlows.IsRedirectAllowed(uri));

    [Fact]
    public void GoogleOAuth_AppliesOnlyInOAuthMode()
    {
        Assert.True(Google.AppliesTo(Settings(("authMode", "OAuth"))));
        Assert.False(Google.AppliesTo(Settings(("authMode", "ServiceAccount"))));
    }

    /// <summary>Endpoint de tokens falso: responde siempre con un refresh token y un id_token.</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler, IHttpClientFactory
    {
        public string LastBody { get; private set; } = string.Empty;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"email":"ana@example.com"}"""));
            var json = $$"""{"access_token":"at","refresh_token":"rt-abc","expires_in":3599,"id_token":"e30.{{payload}}.firma"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
