using YTile.Config;
using YTile.Core;
using YTile.Win32;

namespace YTile.Tests;

[TestClass]
public sealed class ConfigTests
{
    private static WindowSnapshot Window(string exe = "app.exe", string cls = "SomeClass", string title = "Some Title") =>
        new() { Hwnd = 1, IsAlive = true, ExeName = exe, ClassName = cls, Title = title };

    [TestMethod]
    public void BuiltInRules_IgnoreBars()
    {
        var config = new YTileConfig();
        var bar = Window(exe: "komorebi-bar.exe");
        Assert.AreEqual(RuleAction.Ignore, config.RuleFor(in bar));
        var ybar = Window(exe: "YBAR.EXE"); // case-insensitive
        Assert.AreEqual(RuleAction.Ignore, config.RuleFor(in ybar));
        var normal = Window(exe: "chrome.exe");
        Assert.IsNull(config.RuleFor(in normal));
    }

    [TestMethod]
    public void Rules_MatchByFieldAndStrategy()
    {
        var prefix = new WindowRule(RuleField.Class, RuleStrategy.Prefix, "Chrome_WidgetWin_", RuleAction.Float);
        var chromium = Window(cls: "Chrome_WidgetWin_1");
        Assert.IsTrue(prefix.Matches(in chromium));
        var other = Window(cls: "CASCADIA_HOSTING_WINDOW_CLASS");
        Assert.IsFalse(prefix.Matches(in other));

        var regex = new WindowRule(RuleField.Title, RuleStrategy.Regex, @"picture.in.picture", RuleAction.Float);
        var pip = Window(title: "Picture-in-Picture");
        Assert.IsTrue(regex.Matches(in pip));
    }

    [TestMethod]
    public void Load_MissingFile_YieldsDefaults()
    {
        YTileConfig config = YTileConfig.Load(Path.Combine(Path.GetTempPath(), "ytile-none", "nope.json"), out string? error);
        Assert.IsNull(error);
        Assert.AreEqual(8, config.Gap);
        Assert.AreEqual(LayoutKind.Bsp, config.DefaultLayout);
    }

    [TestMethod]
    public void Load_ParsesConfigAndKeepsBuiltIns()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ytile-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            """
            {
              "gap": 12,
              "focusBorderColor": "#FF8000",
              "defaultLayout": "columns",
              "rules": [
                { "match": "exe", "pattern": "Battle.net.exe", "action": "float" },
                { "match": "title", "pattern": "^Steam$", "strategy": "regex", "action": "ignore" }
              ]
            }
            """);
        try
        {
            YTileConfig config = YTileConfig.Load(path, out string? error);
            Assert.IsNull(error);
            Assert.AreEqual(12, config.Gap);
            Assert.AreEqual(LayoutKind.Columns, config.DefaultLayout);
            // #FF8000 -> COLORREF 0x000080FF (BGR)
            Assert.AreEqual(0x000080FFu, config.FocusBorderColor);

            var bnet = Window(exe: "battle.net.exe");
            Assert.AreEqual(RuleAction.Float, config.RuleFor(in bnet));
            var steam = Window(title: "Steam");
            Assert.AreEqual(RuleAction.Ignore, config.RuleFor(in steam));
            var bar = Window(exe: "ybar.exe"); // built-ins still active after user rules
            Assert.AreEqual(RuleAction.Ignore, config.RuleFor(in bar));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_BadJson_FallsBackToDefaultsWithError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ytile-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            YTileConfig config = YTileConfig.Load(path, out string? error);
            Assert.IsNotNull(error);
            Assert.AreEqual(8, config.Gap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TryParseColor_RoundTrips()
    {
        Assert.IsTrue(YTileConfig.TryParseColor("#569CD6", out uint c));
        Assert.AreEqual(0x00D69C56u, c);
        Assert.IsFalse(YTileConfig.TryParseColor("569CD6", out _));
        Assert.IsFalse(YTileConfig.TryParseColor("#56gCD6", out _));
    }
}
