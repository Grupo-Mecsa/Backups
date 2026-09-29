using Backup.Application.Security;
using Microsoft.AspNetCore.Components;

namespace Backup.Web.Services;

/// <summary>
/// URLs absolutas para los correos. Usa <c>Backup:PublicUrl</c> si está configurada (recomendado detrás de un
/// proxy inverso); si no, la URL con la que el usuario abrió la aplicación.
/// </summary>
public sealed class AccountLinks(NavigationManager navigation, IConfiguration configuration) : IAccountLinks
{
    public string SetPassword(string userId, string token, bool invitation) =>
        $"{BaseUrl}account/reset-password?user={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(token)}{(invitation ? "&invite=1" : null)}";

    public string Absolute(string path) => BaseUrl + path.TrimStart('/');

    private string BaseUrl
    {
        get
        {
            if (configuration["Backup:PublicUrl"] is { Length: > 0 } configured)
            {
                return configured.TrimEnd('/') + "/";
            }

            try
            {
                return navigation.BaseUri;
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("No se pudo determinar la URL de la aplicación; configura Backup__PublicUrl.", ex);
            }
        }
    }
}
