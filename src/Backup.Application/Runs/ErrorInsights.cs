using System.Text.RegularExpressions;

namespace Backup.Application.Runs;

/// <summary>Un error explicado: qué pasó en una línea, qué hacer, y el mensaje técnico original.</summary>
/// <param name="Title">Qué pasó, en lenguaje claro (una línea).</param>
/// <param name="Hint">Qué hacer para resolverlo. Null si no se reconoce el error.</param>
/// <param name="Raw">Mensaje técnico tal como se registró.</param>
/// <param name="Recognized">Si se identificó la causa (si no, el título es el inicio del mensaje original).</param>
public sealed record ErrorInsight(string Title, string? Hint, string Raw, bool Recognized);

/// <summary>
/// Traduce los mensajes de error de las ejecuciones (de pg_dump, SMB, FTP, S3…) a una causa y una acción. Trabaja sobre
/// el texto guardado, así que sirve también para ejecuciones antiguas.
/// </summary>
public static partial class ErrorInsights
{
    private sealed record Rule(Regex Pattern, Func<Match, string, (string Title, string Hint)> Explain);

    private static Rule R(string pattern, Func<Match, string, (string, string)> explain) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)), explain);

    private static Rule R(string pattern, string title, string hint) => R(pattern, (_, _) => (title, hint));

    // El orden importa: las reglas específicas van antes que las genéricas (p. ej. RLS antes que "query failed").
    private static readonly Rule[] Rules =
    [
        R(@"(\d+) elemento\(s\) omitido\(s\)", (m, _) => (
            $"{m.Groups[1].Value} archivo(s) o elemento(s) omitido(s)",
            "El respaldo se completó, pero algunos elementos no se pudieron leer (nombres con caracteres especiales, archivos borrados o sin permiso). Abre la bitácora para ver cuáles.")),
        R(@"row-level security", (_, raw) => (
            $"{UserPrefix(raw)}no puede leer tablas protegidas con seguridad por fila (RLS)",
            $"Ejecuta ALTER ROLE {UserName(raw) ?? "<usuario>"} BYPASSRLS; o usa un usuario dedicado a respaldos que ya tenga BYPASSRLS. No uses --enable-row-security: respaldaría solo parte de los datos.")),
        R(@"password authentication failed|Login failed for user|Access denied for user|Authentication failed\.? *\(?mechanism",
            (_, raw) => ($"Usuario o contraseña incorrectos{ForUser(raw)} en la base de datos",
                "Revisa las credenciales en la conexión de origen. Si la contraseña cambió hace poco, actualízala en BackupHub.")),
        R(@"permission denied for (table|schema|sequence|database|relation) ""?([\w.]+)""?", (m, raw) => (
            $"{UserPrefix(raw)}no tiene permiso sobre {m.Groups[1].Value} {m.Groups[2].Value}",
            "Dale permisos de lectura (GRANT SELECT / USAGE) a ese usuario, o usa un usuario de respaldo con acceso a todo el esquema.")),
        R(@"server version mismatch", "La versión de pg_dump es menor que la del servidor PostgreSQL",
            "Reconstruye la imagen con un PG_MAJOR igual o mayor que la versión del servidor."),
        R(@"database ""([^""]+)"" does not exist|Unknown database '([^']+)'|Cannot open database ""([^""]+)""", (m, _) => (
            $"La base de datos «{First(m)}» no existe",
            "Revisa el nombre de la base de datos en el origen (distingue mayúsculas y minúsculas).")),

        R(@"recurso '([^']+)'.*STATUS_BAD_NETWORK_NAME", (m, _) => (
            $"La carpeta compartida «{m.Groups[1].Value}» no existe en el servidor",
            "Revisa el nombre del recurso compartido en la conexión SMB: quizá se renombró o se quitó. Usa «Explorar» para ver los recursos disponibles.")),
        R(@"STATUS_BAD_NETWORK_NAME", "La carpeta compartida no existe en el servidor",
            "Revisa el nombre del recurso compartido en la conexión SMB."),
        R(@"STATUS_LOGON_FAILURE|STATUS_ACCOUNT_(DISABLED|LOCKED_OUT)|STATUS_PASSWORD_EXPIRED", "El servidor SMB rechazó el usuario o la contraseña",
            "Revisa usuario, contraseña y dominio en la conexión SMB. La cuenta podría estar bloqueada o con la contraseña vencida."),
        R(@"STATUS_ACCESS_DENIED", "El usuario no tiene permiso en la carpeta compartida",
            "Dale permiso de lectura (origen) o de escritura (destino) a la cuenta, tanto en el recurso compartido como en NTFS."),
        R(@"STATUS_OBJECT_(PATH|NAME)_NOT_FOUND", "La carpeta indicada no existe en el recurso compartido",
            "Revisa la ruta dentro del recurso compartido o elígela con «Explorar»."),
        R(@"STATUS_DISK_FULL|No space left on device|There is not enough space on the disk|insufficient (disk )?space", "No hay espacio en disco",
            "Libera espacio en el destino o en la carpeta de trabajo de BackupHub, o ajusta la retención."),

        R(@"\b530\b|Login incorrect|Permission denied \(publickey|Authentication failed|Invalid credentials", "El servidor rechazó el usuario o la contraseña",
            "Revisa las credenciales (o la llave privada) en la conexión."),
        R(@"InvalidAccessKeyId|SignatureDoesNotMatch|AuthorizationFailure|AuthenticationFailed|The AWS Access Key Id", "La nube rechazó las credenciales de acceso",
            "Revisa la access key / secret (o la cadena de conexión de Azure). Si se rotaron, actualízalas."),
        R(@"NoSuchBucket|ContainerNotFound|specified (bucket|container) does not exist", "El bucket o contenedor no existe",
            "Revisa el nombre (y la región) del bucket o contenedor en el destino."),
        R(@"\bAccessDenied\b|403 \(?Forbidden", "La nube denegó el acceso",
            "La credencial no tiene permiso sobre el bucket o contenedor: revisa la política (lectura, escritura, listar y borrar)."),

        R(@"remote certificate is invalid|certificate (verify|validation) failed|UntrustedRoot|RemoteCertificateNameMismatch", "El certificado TLS del servidor no es de confianza",
            "Si es un certificado autofirmado, activa «Aceptar cualquier certificado» en la conexión. Si no, revisa que el host coincida con el certificado."),
        R(@"Connection refused|actively refused|ECONNREFUSED", "El servidor rechazó la conexión",
            "Comprueba el host y el puerto, y que el servicio esté encendido y acepte conexiones desde el servidor de BackupHub."),
        R(@"No such host|Name or service not known|could not translate host name|nodename nor servname|Temporary failure in name resolution|Name does not resolve", "No se encontró el servidor (DNS)",
            "Revisa que el nombre del host esté bien escrito y que se resuelva desde el contenedor de BackupHub."),
        R(@"Network is unreachable|No route to host|Host is down", "No hay conexión de red con el servidor",
            "Comprueba la red, la VPN o el firewall entre BackupHub y el servidor."),
        R(@"Error while (downloading|uploading).*(Timed out|timeout)|Timed out trying to (read|write|connect)", (m, _) => (
            $"Se agotó el tiempo de espera al {(m.Value.Contains("upload", StringComparison.OrdinalIgnoreCase) ? "subir" : "descargar")} un archivo",
            "La conexión se cortó o quedó inactiva a mitad de la transferencia (típico con archivos grandes y firewalls o NAS que cierran conexiones inactivas). Vuelve a ejecutarlo; si se repite, revisa la red.")),
        R(@"timed out|timeout|Connection timed out", "Se agotó el tiempo de espera con el servidor",
            "El servidor no respondió a tiempo: comprueba que esté accesible y que no lo bloquee un firewall."),
        R(@"Connection reset|forcibly closed|Broken pipe|unexpectedly disconnected", "El servidor cerró la conexión inesperadamente",
            "Puede ser un corte de red o un límite del servidor. Vuelve a ejecutarlo; si se repite, revisa los logs del servidor."),
        R(@"Interrumpid[oa] por reinicio", "Se interrumpió por un reinicio del servicio",
            "Vuelve a ejecutarlo. Si estaba restaurando, revisa el destino: puede haber quedado a medias."),
    ];

    /// <summary>Explica <paramref name="error"/>. Si no reconoce la causa, el título es el inicio del mensaje.</summary>
    public static ErrorInsight Explain(string error)
    {
        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(error);
            if (match.Success)
            {
                var (title, hint) = rule.Explain(match, error);
                return new ErrorInsight(title, hint, error, Recognized: true);
            }
        }

        return new ErrorInsight(FirstSentence(error), null, error, Recognized: false);
    }

    /// <summary>
    /// Mensaje completo de una excepción y sus causas ("A → B"), sin los textos genéricos que solo remiten a la
    /// excepción interna (p. ej. "See InnerException for more info").
    /// </summary>
    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = GenericSuffix().Replace(current.Message, string.Empty).Trim();
            if (message.Length > 0 && !parts.Any(p => p.Contains(message, StringComparison.Ordinal)))
            {
                parts.Add(message);
            }
        }

        return parts.Count == 0 ? exception.GetType().Name : string.Join(" → ", parts);
    }

    private static string? UserName(string raw)
    {
        var match = UserPattern().Match(raw);
        return match.Success ? First(match) : null;
    }

    private static string UserPrefix(string raw) => UserName(raw) is { } user ? $"El usuario «{user}» " : "El usuario ";

    private static string ForUser(string raw) => UserName(raw) is { } user ? $" para «{user}»" : string.Empty;

    private static string First(Match match) =>
        match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value ?? match.Value;

    private static string FirstSentence(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        if (end > 20)
        {
            line = line[..(end + 1)];
        }

        return line.Length <= 160 ? line : line[..157].TrimEnd() + "…";
    }

    [GeneratedRegex(@"usuario '([^']+)'|for user ""([^""]+)""|for user '([^']+)'|role ""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"\s*See (the )?inner ?exception for (more info|details)\.?", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSuffix();
}
