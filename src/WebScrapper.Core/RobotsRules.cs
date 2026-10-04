using System.Text.RegularExpressions;

namespace WebScrapper.Core;

internal sealed class RobotsRules
{
    private readonly List<(string Pattern, bool Allow)> rules = [];
    public TimeSpan Delay { get; private set; }

    public static RobotsRules Parse(string text, string userAgent)
    {
        var groups = new List<(List<string> Agents, List<(string, bool)> Rules, double Delay)>();
        var agents = new List<string>();
        var entries = new List<(string, bool)>();
        double delay = 0;
        bool hasDirectives = false;
        void Save() { if (agents.Count > 0) groups.Add((agents, entries, delay)); agents = []; entries = []; delay = 0; hasDirectives = false; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent") { if (hasDirectives) Save(); agents.Add(value.ToLowerInvariant()); }
            else if (agents.Count > 0)
            {
                hasDirectives = true;
                if (key is "allow" or "disallow" && value.Length > 0) entries.Add((value, key == "allow"));
                if (key == "crawl-delay" && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= 86400) delay = seconds;
            }
        }
        Save();
        var token = userAgent.Split('/')[0].ToLowerInvariant();
        int Score(List<string> values) => values.Select(a => a == "*" ? 0 : a.Length > 0 && token.Contains(a, StringComparison.Ordinal) ? a.Length : -1).DefaultIfEmpty(-1).Max();
        var best = groups.Select(g => Score(g.Agents)).DefaultIfEmpty(-1).Max();
        var result = new RobotsRules();
        foreach (var group in groups.Where(g => best >= 0 && Score(g.Agents) == best))
        { result.rules.AddRange(group.Rules); result.Delay = TimeSpan.FromSeconds(Math.Max(result.Delay.TotalSeconds, group.Delay)); }
        return result;
    }

    public bool Allows(Uri uri)
    {
        var best = -1; var allow = true;
        foreach (var rule in rules)
        {
            var pattern = "^" + Regex.Escape(rule.Pattern).Replace("\\*", ".*").Replace("\\$", "$" );
            if (!Regex.IsMatch(uri.PathAndQuery, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
            var score = rule.Pattern.Replace("*", "").TrimEnd('$').Length;
            if (score > best || score == best && rule.Allow) { best = score; allow = rule.Allow; }
        }
        return allow;
    }
}
