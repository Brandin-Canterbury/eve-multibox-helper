using System.Text.RegularExpressions;

namespace EveMultibox.Clients;

public sealed record ParsedTitle(bool IsClient, string? CharacterName)
{
    public bool IsLoginScreen => IsClient && CharacterName is null;
    public static readonly ParsedTitle NotAClient = new(false, null);
}

/// <summary>Turns window titles into character names. EVE titles are "EVE - Name" once logged in and "EVE" before.</summary>
public sealed class TitleParser
{
    public const string DefaultPattern = @"^EVE - (?<name>.+)$";
    private const string LoginTitle = "EVE";

    private readonly Regex _regex;
    private readonly bool _includeLogin;

    public TitleParser(string pattern, bool includeLoginScreens)
    {
        _regex = new Regex(string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern,
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        _includeLogin = includeLoginScreens;
    }

    public static bool IsValidPattern(string pattern, out string? error)
    {
        try
        {
            var regex = new Regex(pattern);
            if (!regex.GetGroupNames().Contains("name"))
            {
                error = "The pattern needs a (?<name>...) group for the character name.";
                return false;
            }
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public ParsedTitle Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ParsedTitle.NotAClient;
        title = title.Trim();

        if (title == LoginTitle) return _includeLogin ? new ParsedTitle(true, null) : ParsedTitle.NotAClient;

        var match = _regex.Match(title);
        if (!match.Success) return ParsedTitle.NotAClient;

        string name = match.Groups["name"].Value.Trim();
        return name.Length == 0 ? ParsedTitle.NotAClient : new ParsedTitle(true, name);
    }
}
