namespace Backup.Application.Security;

/// <param name="IsPending">Pidió unirse al tenant con el registro y espera aprobación.</param>
public sealed record UserSummary(
    string Id,
    string Email,
    string DisplayName,
    string Role,
    Guid TenantId,
    bool IsLocked,
    DateTimeOffset? LastLoginAt,
    bool IsPending = false);

/// <param name="SendInvitation">Envía un correo para que el usuario defina su contraseña (entonces <paramref name="Password"/> es opcional).</param>
public sealed record UserEdit(string? Id, string Email, string DisplayName, string Role, string? Password, bool SendInvitation = false);

/// <param name="Notice">Aviso no bloqueante (p. ej. el usuario se creó pero el correo no salió).</param>
public sealed record UserOperationResult(bool Success, IReadOnlyList<string> Errors, string? Notice = null)
{
    public static readonly UserOperationResult Ok = new(true, []);
    public static UserOperationResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>Gestión de usuarios del tenant actual. La implementación vive en infraestructura (ASP.NET Core Identity).</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<UserOperationResult> SaveAsync(UserEdit edit, CancellationToken cancellationToken = default);
    Task<UserOperationResult> SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken = default);
    Task<UserOperationResult> DeleteAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Aprueba una solicitud para unirse al tenant (para rechazarla se elimina el usuario).</summary>
    Task<UserOperationResult> ApproveAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Crea un usuario en otro tenant (solo SuperAdmin), p. ej. el administrador inicial de un tenant nuevo.</summary>
    Task<UserOperationResult> CreateInTenantAsync(Guid tenantId, UserEdit edit, CancellationToken cancellationToken = default);

    /// <summary>Envía al usuario un enlace para definir o restablecer su contraseña.</summary>
    Task<UserOperationResult> SendAccessLinkAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Hay un servidor SMTP con el que enviar correos de cuenta al tenant actual.</summary>
    Task<bool> CanSendEmailAsync(CancellationToken cancellationToken = default);

    /// <summary>Cambia la contraseña del propio usuario.</summary>
    Task<UserOperationResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}

/// <summary>Construye URLs absolutas de la aplicación para incluirlas en correos.</summary>
public interface IAccountLinks
{
    string SetPassword(string userId, string token, bool invitation);

    /// <summary>URL absoluta de una ruta de la aplicación, p. ej. <c>admin/tenants</c>.</summary>
    string Absolute(string path);
}

public enum RegistrationMode
{
    /// <summary>Solo los administradores crean cuentas.</summary>
    Disabled = 0,

    /// <summary>
    /// Registro habilitado: quien crea una organización nueva es su primer usuario y queda aprobado como administrador;
    /// quien pide unirse a una existente queda pendiente hasta que un administrador de esa organización lo apruebe.
    /// </summary>
    Enabled = 1,

    /// <summary>Nombre anterior de <see cref="Enabled"/> (compatibilidad con configuraciones existentes).</summary>
    Approval = Enabled,

    /// <summary>Nombre anterior de <see cref="Enabled"/> (compatibilidad con configuraciones existentes).</summary>
    Open = Enabled,
}

/// <summary>Autorregistro (sección <c>Registration</c>).</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    public RegistrationMode Mode { get; set; } = RegistrationMode.Enabled;
}
