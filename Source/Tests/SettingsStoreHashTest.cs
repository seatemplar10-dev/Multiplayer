using FluentAssertions;
using Multiplayer.Client.Util;

namespace Tests;

// FIXES row 512: SettingsStoreHash (store list, hashes, ignored mods, diff).
[TestFixture]
public class SettingsStoreHashTest
{
    private static string MakeRoot(params (string rel, string text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "row512_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return root;
    }

    private static readonly (string, string)[] Base =
    [
        ("Config/Mod_111_RjwMod.xml", "a"), ("Config/Mod_222_Other.xml", "b"), ("Config/Mod_999_Ignored.xml", "c"),
        ("Config/ModSettingsFrameworkMod_Settings.xml", "m"), ("Config/KeyPrefs.xml", "k"), ("Config/ModsConfig.xml", "x"),
        ("HugsLib/ModSettings.xml", "h"),
    ];

    [Test]
    public void ListsStoresAndSkipsIgnoredAndNonStores()
    {
        var root = MakeRoot(Base);
        var files = SettingsStoreHash.ListStoreFiles(root, ["999"]);
        files.Should().Equal("Config/ModSettingsFrameworkMod_Settings.xml", "Config/Mod_111_RjwMod.xml",
            "Config/Mod_222_Other.xml", "HugsLib/ModSettings.xml");
    }

    [Test]
    public void LinesCarrySha256AndAreSorted()
    {
        var root = MakeRoot(("Config/Mod_1_A.xml", "abc"));
        var lines = SettingsStoreHash.ComputeLines(root, null);
        lines.Should().Equal("Config/Mod_1_A.xml\tba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public void EqualFoldersAreNotFlagged()
    {
        var a = SettingsStoreHash.ComputeLines(MakeRoot(Base), null);
        var b = SettingsStoreHash.ComputeLines(MakeRoot(Base), null);
        SettingsStoreHash.Diff(a, b).Should().BeEmpty();
    }

    [Test]
    public void DifferentModFileIsFlagged()
    {
        var other = Base.Select(f => f.Item1 == "Config/Mod_111_RjwMod.xml" ? (f.Item1, "changed") : f).ToArray();
        SettingsStoreHash.Diff(SettingsStoreHash.ComputeLines(MakeRoot(Base), null), SettingsStoreHash.ComputeLines(MakeRoot(other), null))
            .Should().Equal("Config/Mod_111_RjwMod.xml");
    }

    [Test]
    public void DifferentMsfFileIsFlagged()
    {
        var other = Base.Select(f => f.Item1.Contains("ModSettingsFramework") ? (f.Item1, "changed") : f).ToArray();
        SettingsStoreHash.Diff(SettingsStoreHash.ComputeLines(MakeRoot(Base), null), SettingsStoreHash.ComputeLines(MakeRoot(other), null))
            .Should().Equal("Config/ModSettingsFrameworkMod_Settings.xml");
    }

    [Test]
    public void MissingFileIsFlaggedBothWays()
    {
        var fewer = Base.Where(f => f.Item1 != "Config/Mod_222_Other.xml").ToArray();
        var a = SettingsStoreHash.ComputeLines(MakeRoot(Base), null);
        var b = SettingsStoreHash.ComputeLines(MakeRoot(fewer), null);
        SettingsStoreHash.Diff(a, b).Should().Equal("Config/Mod_222_Other.xml");
        SettingsStoreHash.Diff(b, a).Should().Equal("Config/Mod_222_Other.xml");
    }

    [Test]
    public void IgnoredModDifferenceIsNotFlagged()
    {
        var other = Base.Select(f => f.Item1.Contains("Mod_999") ? (f.Item1, "changed") : f).ToArray();
        SettingsStoreHash.Diff(SettingsStoreHash.ComputeLines(MakeRoot(Base), ["999"]), SettingsStoreHash.ComputeLines(MakeRoot(other), ["999"]))
            .Should().BeEmpty();
    }

    [Test]
    public void PackRoundTripsAndKinds()
    {
        var lines = SettingsStoreHash.ComputeLines(MakeRoot(Base), null);
        SettingsStoreHash.Unpack(SettingsStoreHash.Pack(lines)).Should().Equal(lines);
        SettingsStoreHash.Unpack("").Should().BeEmpty();
        SettingsStoreHash.Kind("Config/ModSettingsFrameworkMod_Settings.xml").Should().Be("MSF");
        SettingsStoreHash.Kind("Config/Mod_1_A.xml").Should().Be("Mod_*.xml");
        SettingsStoreHash.Kind("HugsLib/ModSettings.xml").Should().Be("HugsLib");
    }
}
