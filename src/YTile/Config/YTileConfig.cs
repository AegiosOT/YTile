using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YTile.Core;
using YTile.Win32;

namespace YTile.Config;

internal sealed record RuleDto(string? Match, string? Pattern, string? Strategy, string? Action);

internal sealed record ConfigDto(int? Gap, string? FocusBorderColor, string? DefaultLayout, int? ResizeStep, bool? HideTaskbar, List<RuleDto>? Rules);

// AllowDuplicateProperties = false: a duplicated property anywhere in
// ytile.json must be a reported config error, not a silent last-one-wins.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ConfigDto))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

internal enum RuleAction
{
    Ignore,
    Float,
}

internal enum RuleField
{
    Exe,
    Class,
    Title,
}

internal enum RuleStrategy
{
    Equals,
    Prefix,
    Regex,
}

internal sealed class WindowRule(RuleField field, RuleStrategy strategy, string pattern, RuleAction action)
{
    private readonly Regex? _regex = strategy == RuleStrategy.Regex
        ? new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        : null;

    public RuleAction Action { get; } = action;

    public bool Matches(in WindowSnapshot w)
    {
        string value = field switch
        {
            RuleField.Exe => w.ExeName,
            RuleField.Class => w.ClassName,
            _ => w.Title,
        };

        return strategy switch
        {
            RuleStrategy.Equals => value.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            RuleStrategy.Prefix => value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            _ => _regex!.IsMatch(value),
        };
    }
}

/// <summary>
/// Loaded once at startup and swapped atomically on `ytile reload` — no
/// mutable global config state, no half-applied reloads.
/// </summary>
internal sealed class YTileConfig
{
    /// <summary>
    /// Windows that must never be tiled, whatever the user's config says.
    /// Status bars, because tiling the bar defeats the point; OS security
    /// prompts and installers, because both are transient popups the user is
    /// mid-interaction with — squeezing one into a cell retiles every real
    /// window around it, then retiles again when it closes.
    /// </summary>
    private static readonly WindowRule[] BuiltInRules =
    [
        new(RuleField.Exe, RuleStrategy.Equals, "komorebi-bar.exe", RuleAction.Ignore),
        new(RuleField.Exe, RuleStrategy.Equals, "ybar.exe", RuleAction.Ignore),
        new(RuleField.Exe, RuleStrategy.Equals, "zebar.exe", RuleAction.Ignore),
        new(RuleField.Exe, RuleStrategy.Equals, "yasb.exe", RuleAction.Ignore),

        // Windows Security: Hello PIN, passkeys, smart cards.
        new(RuleField.Exe, RuleStrategy.Equals, "CredentialUIBroker.exe", RuleAction.Ignore),
        new(RuleField.Class, RuleStrategy.Equals, "Credential Dialog Xaml Host", RuleAction.Ignore),

        // Installers. msiexec covers every MSI (install AND uninstall); the
        // wizard classes catch MSI's and Inno Setup's own windows whatever the
        // exe is called; the name pattern catches the NSIS/bundle convention
        // (setup.exe, App-1.2-setup.exe, unins000.exe). Deliberately NOT
        // matching class "#32770" — that is the generic Win32 dialog class,
        // shared with countless windows that should still tile.
        new(RuleField.Exe, RuleStrategy.Equals, "msiexec.exe", RuleAction.Ignore),
        new(RuleField.Exe, RuleStrategy.Regex, InstallerExePattern, RuleAction.Ignore),
        new(RuleField.Class, RuleStrategy.Equals, "MsiDialogCloseClass", RuleAction.Ignore),
        new(RuleField.Class, RuleStrategy.Equals, "TWizardForm", RuleAction.Ignore),
        new(RuleField.Class, RuleStrategy.Equals, "TUninstallProgressForm", RuleAction.Ignore),
        new(RuleField.Class, RuleStrategy.Prefix, "InstallShield", RuleAction.Ignore),
    ];

    /// <summary>Anchored so it matches installer names, not apps that merely
    /// contain the word: "setup.exe" and "vlc-3.0-setup.exe" match,
    /// "SetupDesigner.exe" does not.</summary>
    private const string InstallerExePattern =
        @"^(setup|install|installer|uninstall|unins\d*|vc_redist\.[^.]+|.+[-_ ](setup|install|installer))\.exe$";

    public int Gap { get; private init; } = 8;

    // COLORREF is 0x00BBGGRR — default is #FFFFFF (white).
    public uint FocusBorderColor { get; private init; } = 0x00FFFFFF;

    public LayoutKind DefaultLayout { get; private init; } = LayoutKind.Bsp;

    /// <summary>Default pixel amount for `ytile resize` without an explicit px.</summary>
    public int ResizeStep { get; private init; } = 50;

    /// <summary>
    /// Hide the shell taskbar entirely and tile over the space it occupied.
    /// Restored whenever YTile pauses or exits.
    /// </summary>
    public bool HideTaskbar { get; private init; }

    public IReadOnlyList<WindowRule> Rules { get; private init; } = BuiltInRules;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ytile", "ytile.json");

    /// <summary>First matching rule wins; user rules take precedence over built-ins.</summary>
    public RuleAction? RuleFor(in WindowSnapshot w)
    {
        foreach (WindowRule rule in Rules)
        {
            if (rule.Matches(in w))
            {
                return rule.Action;
            }
        }

        return null;
    }

    /// <summary>Never throws: parse problems land in <paramref name="error"/> and defaults apply.</summary>
    public static YTileConfig Load(string? path, out string? error)
    {
        error = null;
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new YTileConfig();
        }

        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.ConfigDto);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            error = $"{path}: {ex.Message}";
            return new YTileConfig();
        }

        if (dto is null)
        {
            return new YTileConfig();
        }

        var rules = new List<WindowRule>();
        var problems = new List<string>();
        foreach (RuleDto rule in dto.Rules ?? [])
        {
            RuleField? field = rule.Match?.ToLowerInvariant() switch
            {
                "exe" => RuleField.Exe,
                "class" => RuleField.Class,
                "title" => RuleField.Title,
                _ => null,
            };
            RuleStrategy? strategy = (rule.Strategy ?? "equals").ToLowerInvariant() switch
            {
                "equals" => RuleStrategy.Equals,
                "prefix" => RuleStrategy.Prefix,
                "regex" => RuleStrategy.Regex,
                _ => null,
            };
            RuleAction? action = rule.Action?.ToLowerInvariant() switch
            {
                "ignore" => RuleAction.Ignore,
                "float" => RuleAction.Float,
                _ => null,
            };

            if (field is null || strategy is null || action is null || string.IsNullOrEmpty(rule.Pattern))
            {
                problems.Add($"bad rule (match={rule.Match}, strategy={rule.Strategy}, action={rule.Action})");
                continue;
            }

            try
            {
                rules.Add(new WindowRule(field.Value, strategy.Value, rule.Pattern, action.Value));
            }
            catch (ArgumentException ex)
            {
                problems.Add($"bad regex '{rule.Pattern}': {ex.Message}");
            }
        }
        rules.AddRange(BuiltInRules);

        uint borderColor = 0x00FFFFFF;
        if (dto.FocusBorderColor is not null && !TryParseColor(dto.FocusBorderColor, out borderColor))
        {
            problems.Add($"bad color '{dto.FocusBorderColor}' (expected #RRGGBB)");
            borderColor = 0x00FFFFFF;
        }

        int resizeStep = 50;
        if (dto.ResizeStep is int rs)
        {
            if (rs is >= 1 and <= 500)
            {
                resizeStep = rs;
            }
            else
            {
                problems.Add($"bad resizeStep {rs} (expected 1..500)");
            }
        }

        if (problems.Count > 0)
        {
            error = $"{path}: {string.Join("; ", problems)}";
        }

        return new YTileConfig
        {
            Gap = dto.Gap ?? 8,
            FocusBorderColor = borderColor,
            DefaultLayout = dto.DefaultLayout?.ToLowerInvariant() == "columns" ? LayoutKind.Columns : LayoutKind.Bsp,
            ResizeStep = resizeStep,
            HideTaskbar = dto.HideTaskbar ?? false,
            Rules = rules,
        };
    }

    /// <summary>"#RRGGBB" → COLORREF (0x00BBGGRR).</summary>
    public static bool TryParseColor(string text, out uint colorref)
    {
        colorref = 0;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return false;
        }

        colorref = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        return true;
    }
}
