using System.Globalization;
using SMBLibrary;

namespace Backup.Providers.Files.Smb;

/// <summary>Traduce los códigos NTSTATUS más comunes a mensajes accionables.</summary>
internal static class SmbErrors
{
    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0xC0000022] = "acceso denegado; revisa los permisos del usuario sobre el recurso y la carpeta",
        [0xC000006D] = "usuario o contraseña incorrectos (revisa también el dominio)",
        [0xC000006E] = "la cuenta tiene restricciones de inicio de sesión",
        [0xC0000071] = "la contraseña de la cuenta expiró",
        [0xC0000072] = "la cuenta está deshabilitada",
        [0xC0000234] = "la cuenta está bloqueada",
        [0xC00000CC] = "el recurso compartido no existe en el servidor",
        [0xC0000034] = "el archivo o carpeta no existe",
        [0xC000003A] = "la ruta no existe",
        [0xC0000043] = "el archivo está en uso por otro proceso",
        [0xC000007F] = "no hay espacio suficiente en el disco del servidor",
        [0xC000026E] = "el volumen del recurso compartido está desmontado o desconectado en el servidor " +
                       "(p. ej. un disco externo retirado); vuelve a conectarlo o apunta el recurso a otra unidad",
        [0xC00000BE] = "no se encontró la ruta de red",
        [0xC0000203] = "el servidor cerró la sesión; intenta de nuevo",
    };

    public static string Describe(NTStatus status)
    {
        var code = (uint)status;
        var name = Enum.IsDefined(status) ? status.ToString() : "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        return Messages.TryGetValue(code, out var message) ? $"{message} ({name})" : name;
    }
}
