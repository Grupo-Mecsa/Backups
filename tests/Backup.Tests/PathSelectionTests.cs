using Backup.Application.Providers;

namespace Backup.Tests;

public class PathSelectionTests
{
    [Fact]
    public void Empty_IncludesEverything()
    {
        var selection = PathSelection.Empty;
        Assert.True(selection.IsIncluded("a/b/c.txt"));
        Assert.True(selection.ShouldDescend("a"));
        Assert.Equal(string.Empty, selection.Serialize());
    }

    [Fact]
    public void ExcludingFolder_ExcludesContents_ButMoreSpecificIncludeWins()
    {
        var selection = PathSelection.Empty;
        selection.Set("docs", included: false);
        selection.Set("docs/importantes", included: true);

        Assert.False(selection.IsIncluded("docs/borrador.txt"));
        Assert.True(selection.IsIncluded("docs/importantes/contrato.pdf"));
        Assert.True(selection.ShouldDescend("docs"));   // contiene algo incluido
        Assert.True(selection.IsPartial("docs"));
        Assert.True(selection.IsIncluded("fotos/a.jpg"));
    }

    [Fact]
    public void UncheckingRoot_ThenCheckingItems_IncludesOnlyThose()
    {
        var selection = PathSelection.Empty;
        selection.Set("", included: false);
        selection.Set("ventas/2026", included: true);

        Assert.False(selection.IsIncluded("rrhh/nomina.xlsx"));
        Assert.False(selection.ShouldDescend("rrhh"));
        Assert.True(selection.IsIncluded("ventas/2026/enero.xlsx"));
        Assert.False(selection.IsIncluded("ventas/2025/enero.xlsx"));
        Assert.True(selection.IsPartial(""));
    }

    [Fact]
    public void Set_RemovesRedundantDescendantRules_AndRoundTrips()
    {
        var selection = PathSelection.Empty;
        selection.Set("a/b", included: false);
        selection.Set("a", included: false);   // la regla de a/b ya sobra
        Assert.Equal("-a", selection.Serialize());

        selection.Set("a", included: true);    // vuelve al estado heredado: sin reglas
        Assert.True(selection.IsEmpty);

        var parsed = PathSelection.Parse("-\n+x/y\n-x/y/tmp");
        Assert.Equal("-\n+x/y\n-x/y/tmp", parsed.Serialize());
    }

    [Theory]
    [InlineData("/backups", "/backups/a/b.txt", "a/b.txt")]
    [InlineData("C:\\Datos", "C:\\Datos\\sub\\f.txt", "sub/f.txt")]
    [InlineData("", "/x/y", "x/y")]
    [InlineData("prefijo", "prefijo", "")]
    public void Relative_NormalizesSeparators(string root, string path, string expected) =>
        Assert.Equal(expected, PathSelection.Relative(root, path));
}
