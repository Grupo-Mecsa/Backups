using System.Globalization;

namespace Backup.Application.Providers;

public enum SettingFieldType
{
    Text,
    Password,
    Number,
    Toggle,
    Select,
    TextArea,

    /// <summary>Reglas de <see cref="PathSelection"/>; la UI las edita con un explorador de casillas.</summary>
    PathSelection,
}

/// <summary>
/// Describe un campo de configuración de un proveedor. La UI construye los formularios
/// a partir de estos metadatos, así que agregar un proveedor no requiere tocar la UI (OCP).
/// </summary>
public sealed record SettingField(string Key, string Label, SettingFieldType Type = SettingFieldType.Text)
{
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Si el proveedor implementa <see cref="IFolderBrowser"/>, la UI ofrece un explorador para este campo.</summary>
    public BrowseMode Browse { get; init; }

    /// <summary>Para <see cref="SettingFieldType.PathSelection"/>: clave del campo con la carpeta raíz a explorar.</summary>
    public string? BrowseRootField { get; init; }

    public static SettingField Selection(string key, string rootField) =>
        new(key, "Archivos y carpetas", SettingFieldType.PathSelection)
        {
            BrowseRootField = rootField,
            Help = "Elige en el explorador qué se incluye y qué se excluye. Por defecto se incluye todo.",
        };

    public SettingField Browsable(BrowseMode mode = BrowseMode.Folder) => this with { Browse = mode };

    /// <summary>
    /// Dato de acceso (servidor, usuario, contraseña...) que puede guardarse en una conexión reutilizable.
    /// Los demás campos (carpeta, base de datos, selección...) son siempre propios de cada trabajo.
    /// </summary>
    public bool IsConnection { get; init; }

    public SettingField ForConnection() => this with { IsConnection = true };

    /// <summary>Si el proveedor implementa <see cref="IOptionLister"/>, la UI ofrece elegir el valor de una lista.</summary>
    public ListMode List { get; init; }

    public SettingField Listable(bool multiple = false) => this with { List = multiple ? ListMode.Multiple : ListMode.Single };

    /// <summary>Los valores secretos se cifran en reposo y se enmascaran en la UI.</summary>
    public bool IsSecret => Type == SettingFieldType.Password;

    public static SettingField Text(string key, string label, bool required = false, string? placeholder = null, string? help = null, string? defaultValue = null) =>
        new(key, label) { Required = required, Placeholder = placeholder, Help = help, DefaultValue = defaultValue };

    public static SettingField Secret(string key, string label, bool required = false, string? help = null) =>
        new(key, label, SettingFieldType.Password) { Required = required, Help = help };

    public static SettingField Number(string key, string label, int defaultValue, string? help = null) =>
        new(key, label, SettingFieldType.Number) { DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture), Help = help };

    public static SettingField Toggle(string key, string label, bool defaultValue = false, string? help = null) =>
        new(key, label, SettingFieldType.Toggle) { DefaultValue = defaultValue ? "true" : "false", Help = help };

    public static SettingField Select(string key, string label, IReadOnlyList<string> options, string? defaultValue = null, string? help = null) =>
        new(key, label, SettingFieldType.Select) { Options = options, DefaultValue = defaultValue ?? options.FirstOrDefault(), Help = help };

    public static SettingField TextArea(string key, string label, string? placeholder = null, string? help = null) =>
        new(key, label, SettingFieldType.TextArea) { Placeholder = placeholder, Help = help };
}
