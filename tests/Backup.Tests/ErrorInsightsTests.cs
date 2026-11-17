using Backup.Application.Runs;
using FluentFTP.Exceptions;

namespace Backup.Tests;

/// <summary>Mensajes reales de ejecuciones fallidas y la explicación que se muestra.</summary>
public sealed class ErrorInsightsTests
{
    [Fact]
    public void Rls_names_the_user_and_the_fix()
    {
        var insight = ErrorInsights.Explain("""
            'pg_dump' terminó con código 1. pg_dump: error: query failed: ERROR:  query would be affected by row-level security policy for table "Account"
            Hay tablas con seguridad por fila (RLS) y el usuario 'pasante1' no puede saltarse sus políticas.
            """);

        Assert.True(insight.Recognized);
        Assert.Contains("«pasante1»", insight.Title);
        Assert.Contains("ALTER ROLE pasante1 BYPASSRLS", insight.Hint);
    }

    [Fact]
    public void Missing_smb_share_names_the_share()
    {
        var insight = ErrorInsights.Explain("No se pudo abrir el recurso 'Backup': el recurso compartido no existe en el servidor (STATUS_BAD_NETWORK_NAME)");

        Assert.Equal("La carpeta compartida «Backup» no existe en el servidor", insight.Title);
    }

    [Fact]
    public void Ftp_download_timeout_is_explained()
    {
        var insight = ErrorInsights.Explain("Error while downloading the file from the server. → Timed out trying to read data from the socket stream!");

        Assert.Equal("Se agotó el tiempo de espera al descargar un archivo", insight.Title);
    }

    [Fact]
    public void Wrong_database_password_names_the_user()
    {
        var insight = ErrorInsights.Explain("""pg_dump: error: connection to server at "db.example.com" (1.2.3.4), port 5432 failed: FATAL:  password authentication failed for user "pasante1" """);

        Assert.Equal("Usuario o contraseña incorrectos para «pasante1» en la base de datos", insight.Title);
    }

    [Fact]
    public void Omitted_items_are_a_warning_with_count()
    {
        var insight = ErrorInsights.Explain("4 elemento(s) omitido(s); revisa la bitácora.");

        Assert.StartsWith("4 archivo(s)", insight.Title);
    }

    [Fact]
    public void Unknown_errors_keep_the_first_sentence()
    {
        var insight = ErrorInsights.Explain("Algo raro pasó en el proveedor. Más detalles aquí.");

        Assert.False(insight.Recognized);
        Assert.Equal("Algo raro pasó en el proveedor.", insight.Title);
        Assert.Null(insight.Hint);
    }

    [Fact]
    public void Describe_includes_inner_exception_and_drops_generic_text()
    {
        var ex = new FtpException("Error while downloading the file from the server. See InnerException for more info.",
            new TimeoutException("Timed out trying to read data from the socket stream!"));

        Assert.Equal("Error while downloading the file from the server. → Timed out trying to read data from the socket stream!", ErrorInsights.Describe(ex));
    }
}
