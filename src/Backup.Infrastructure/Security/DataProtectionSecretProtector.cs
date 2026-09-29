using Backup.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Backup.Infrastructure.Security;

/// <summary>
/// Protege secretos con ASP.NET Core Data Protection. Las llaves viven en el directorio de datos,
/// por lo que ese directorio debe persistir (volumen de Docker).
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _protector = provider.CreateProtector("Backup.Secrets.v1");

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith(Prefix, StringComparison.Ordinal)
            ? _protector.Unprotect(protectedValue[Prefix.Length..])
            : protectedValue;
}
