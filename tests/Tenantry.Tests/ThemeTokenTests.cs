using System.Xml.Linq;

namespace Tenantry.Tests;

public class ThemeTokenTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tenantry.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    // A key defined in one theme only resolves to nothing in the other, silently: the control
    // just loses its colour. So both dictionaries must carry exactly the same keys.
    [Fact]
    public void Light_and_dark_define_the_same_tokens()
    {
        var doc = XDocument.Load(RepoFile("src", "Tenantry.App", "Themes", "Tokens.axaml"));
        var themes = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") is not null)
            .ToDictionary(
                e => e.Attribute(X + "Key")!.Value,
                e => e.Elements().Select(c => c.Attribute(X + "Key")?.Value).OfType<string>().ToHashSet());

        Assert.Equal(["Dark", "Light"], themes.Keys.Order());
        Assert.Empty(themes["Light"].Except(themes["Dark"]));
        Assert.Empty(themes["Dark"].Except(themes["Light"]));
        Assert.True(themes["Light"].Count > 20, "token parsing found suspiciously few keys");
    }

    // Every Badge kind the view models can produce must have its two brushes in the tokens.
    [Fact]
    public void Every_badge_kind_has_colours()
    {
        var text = File.ReadAllText(RepoFile("src", "Tenantry.App", "Themes", "Tokens.axaml"));

        foreach (var kind in Enum.GetNames<Tenantry.ViewModels.BadgeKind>())
        {
            Assert.Contains($"\"Badge{kind}Bg\"", text);
            Assert.Contains($"\"Badge{kind}Fg\"", text);
        }
    }
}
