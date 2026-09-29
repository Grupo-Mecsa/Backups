namespace Backup.Web.Components.Atoms;

public sealed record SelectOption(string Value, string Label)
{
    public static IEnumerable<SelectOption> From(IEnumerable<string> values) => values.Select(v => new SelectOption(v, v));
}
