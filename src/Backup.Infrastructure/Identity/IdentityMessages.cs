using Microsoft.AspNetCore.Identity;

namespace Backup.Infrastructure.Identity;

/// <summary>Mensajes de validación de Identity en español.</summary>
internal static class IdentityMessages
{
    public static string Translate(IdentityError error) => error.Code switch
    {
        nameof(IdentityErrorDescriber.PasswordTooShort) => "La contraseña es demasiado corta (mínimo 8 caracteres).",
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "La contraseña debe incluir al menos un número.",
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => "La contraseña debe incluir al menos una minúscula.",
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "La contraseña debe incluir al menos una mayúscula.",
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => "La contraseña debe incluir al menos un símbolo.",
        nameof(IdentityErrorDescriber.PasswordMismatch) => "La contraseña actual no es correcta.",
        nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail) => "Ya existe un usuario con ese correo.",
        nameof(IdentityErrorDescriber.InvalidEmail) => "El correo no es válido.",
        _ => error.Description,
    };
}
