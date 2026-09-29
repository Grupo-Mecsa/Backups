namespace Backup.Application.Providers;

/// <summary>
/// Selección de archivos y carpetas a respaldar, relativa a la carpeta raíz del origen.
/// Cada regla incluye (+) o excluye (-) una ruta y todo lo que contiene; gana la regla más específica.
/// Sin reglas se incluye todo. La regla de raíz ("-" sola) excluye todo salvo lo incluido explícitamente.
/// </summary>
/// <remarks>Se serializa como una regla por línea: <c>+docs/2026</c>, <c>-docs/tmp</c>, <c>-</c>.</remarks>
public sealed class PathSelection
{
    private readonly Dictionary<string, bool> _rules;

    private PathSelection(Dictionary<string, bool> rules) => _rules = rules;

    public static PathSelection Empty => new(new Dictionary<string, bool>(StringComparer.Ordinal));

    public bool IsEmpty => _rules.Count == 0;

    public IEnumerable<string> Included => _rules.Where(r => r.Value).Select(r => r.Key).Order(StringComparer.Ordinal);

    public IEnumerable<string> Excluded => _rules.Where(r => !r.Value).Select(r => r.Key).Order(StringComparer.Ordinal);

    public static PathSelection Parse(string? value)
    {
        var rules = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var raw in (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw[0] is '+' or '-')
            {
                rules[Normalize(raw[1..])] = raw[0] == '+';
            }
        }

        return new PathSelection(rules);
    }

    public string Serialize() => string.Join('\n', _rules
        .OrderBy(r => r.Key, StringComparer.Ordinal)
        .Select(r => (r.Value ? "+" : "-") + r.Key));

    /// <summary>¿Se respalda este archivo o carpeta?</summary>
    public bool IsIncluded(string relativePath) => Evaluate(Normalize(relativePath), skipExact: false);

    /// <summary>¿Hay que entrar en esta carpeta? (está incluida o contiene algo incluido).</summary>
    public bool ShouldDescend(string relativeDirectory)
    {
        var path = Normalize(relativeDirectory);
        return Evaluate(path, skipExact: false) || _rules.Any(r => r.Value && IsBelow(r.Key, path));
    }

    /// <summary>Hay reglas dentro de la carpeta que contradicen su estado (casilla "a medias").</summary>
    public bool IsPartial(string relativeDirectory)
    {
        var path = Normalize(relativeDirectory);
        var state = Evaluate(path, skipExact: false);
        return _rules.Any(r => r.Value != state && IsBelow(r.Key, path));
    }

    /// <summary>Marca o desmarca una ruta: descarta las reglas de sus descendientes y agrega una regla solo si hace falta.</summary>
    public void Set(string relativePath, bool included)
    {
        var path = Normalize(relativePath);
        foreach (var key in _rules.Keys.Where(k => k == path || IsBelow(k, path)).ToList())
        {
            _rules.Remove(key);
        }

        if (Evaluate(path, skipExact: true) != included)
        {
            _rules[path] = included;
        }
    }

    public PathSelection Clone() => new(new Dictionary<string, bool>(_rules, StringComparer.Ordinal));

    /// <summary>Ruta de <paramref name="path"/> relativa a <paramref name="root"/>, con '/' como separador.</summary>
    public static string Relative(string? root, string path)
    {
        var cleanRoot = Normalize(root);
        var cleanPath = Normalize(path);
        if (cleanRoot.Length == 0)
        {
            return cleanPath;
        }

        return cleanPath == cleanRoot ? string.Empty
            : cleanPath.StartsWith(cleanRoot + "/", StringComparison.OrdinalIgnoreCase) ? cleanPath[(cleanRoot.Length + 1)..]
            : cleanPath;
    }

    private bool Evaluate(string path, bool skipExact)
    {
        string? best = null;
        foreach (var key in _rules.Keys)
        {
            var matches = key.Length == 0 || key == path || path.StartsWith(key + "/", StringComparison.Ordinal);
            if (matches && !(skipExact && key == path) && (best is null || key.Length > best.Length))
            {
                best = key;
            }
        }

        return best is null || _rules[best];
    }

    private static bool IsBelow(string candidate, string ancestor) =>
        ancestor.Length == 0 ? candidate.Length > 0 : candidate.StartsWith(ancestor + "/", StringComparison.Ordinal);

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}
