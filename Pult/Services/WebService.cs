using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pult.Services;

// Поиск в интернете без NuGet: DuckDuckGo html/lite + Bing fallback.
// Порт tools/web.py из питона.
public static class WebService
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        + "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClientHandler { AllowAutoRedirect = true };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.9");
        return c;
    }

    public static string OpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return "Ошибка: пустой URL.";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            return SystemMonitor.OpenDeElevated(url);
        }
        catch (Exception ex) { return $"Ошибка открытия URL: {ex.Message}"; }
    }

    public static string OpenSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Ошибка: пустой запрос.";
        string url = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);
        return OpenUrl(url);
    }

    private static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string s = Regex.Replace(html, "<.*?>", " ");
        s = s.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#x27;", "'")
            .Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&nbsp;", " ");
        s = Regex.Replace(s, "\\s+", " ").Trim();
        return s;
    }

    private static string UnwrapDdg(string link)
    {
        if (string.IsNullOrEmpty(link)) return link;
        try
        {
            int q = link.IndexOf('?');
            if (q >= 0 && link.Contains("duckduckgo.com"))
            {
                var m = Regex.Match(link, "[?&]uddg=([^&]+)");
                if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
            }
        }
        catch { }
        if (link.StartsWith("//")) link = "https:" + link;
        return link;
    }

    private static List<(string Title, string Url, string Snippet)> ParseDdgHtml(string html, int max)
    {
        var res = new List<(string, string, string)>();
        // Блоки .result с .result__a и .result__snippet
        foreach (Match m in Regex.Matches(html,
            @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (res.Count >= max) break;
            string url = UnwrapDdg(m.Groups[1].Value);
            string title = StripTags(m.Groups[2].Value);
            if (title.Length == 0 || url.Length == 0) continue;
            res.Add((title, url, ""));
        }
        // Сниппеты дотягиваем по порядку
        var snips = Regex.Matches(html,
            @"<[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</",
            RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Cast<Match>().Select(x => StripTags(x.Groups[1].Value)).ToList();
        for (int i = 0; i < res.Count && i < snips.Count; i++)
            res[i] = (res[i].Item1, res[i].Item2, snips[i]);
        return res;
    }

    private static List<(string Title, string Url, string Snippet)> ParseDdgLite(string html, int max)
    {
        var res = new List<(string, string, string)>();
        foreach (Match m in Regex.Matches(html,
            @"<a[^>]*class=""[^""]*result-link[^""]*""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (res.Count >= max) break;
            string url = UnwrapDdg(m.Groups[1].Value);
            string title = StripTags(m.Groups[2].Value);
            if (title.Length == 0 || url.Length == 0) continue;
            res.Add((title, url, ""));
        }
        return res;
    }

    private static List<(string Title, string Url, string Snippet)> ParseBing(string html, int max)
    {
        var res = new List<(string, string, string)>();
        foreach (Match li in Regex.Matches(html,
            @"<li[^>]*class=""[^""]*b_algo[^""]*""[^>]*>(.*?)</li>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (res.Count >= max) break;
            var a = Regex.Match(li.Groups[1].Value,
                @"<h2[^>]*>.*?<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!a.Success) continue;
            string title = StripTags(a.Groups[2].Value);
            string url = a.Groups[1].Value;
            if (title.Length == 0 || url.Length == 0) continue;
            var sn = Regex.Match(li.Groups[1].Value,
                @"<(?:p|div)[^>]*class=""[^""]*b_caption[^""]*""[^>]*>(.*?)</(?:p|div)>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            res.Add((title, url, sn.Success ? StripTags(sn.Groups[1].Value) : ""));
        }
        return res;
    }

    public static string SearchRead(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Ошибка: пустой запрос.";
        maxResults = Math.Clamp(maxResults, 1, 10);
        string q = Uri.EscapeDataString(query);
        var providers = new (string Name, string Url, Func<string, int, List<(string, string, string)>> Parse)[]
        {
            ("DuckDuckGo", "https://html.duckduckgo.com/html/?q=" + q, ParseDdgHtml),
            ("DuckDuckGo Lite", "https://lite.duckduckgo.com/lite/?q=" + q, ParseDdgLite),
            ("Bing", "https://www.bing.com/search?q=" + q + "&setlang=ru", ParseBing),
        };
        var errors = new List<string>();
        foreach (var (name, url, parse) in providers)
        {
            string html;
            try
            {
                html = Http.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {FriendlyNetError(ex)}");
                continue;
            }
            List<(string Title, string Url, string Snippet)> found;
            try { found = parse(html, maxResults); }
            catch (Exception ex) { errors.Add($"{name}: парсинг не удался ({ex.Message})"); continue; }
            if (found.Count > 0)
            {
                var lines = new List<string>
                {
                    $"Результаты «{query}» ({name}, топ-{found.Count}):",
                };
                foreach (var item in found)
                    lines.Add(item.Item3.Length > 0
                        ? $"— {item.Item1}\n  {item.Item2}\n  {item.Item3}"
                        : $"— {item.Item1}\n  {item.Item2}");
                return string.Join("\n\n", lines);
            }
            errors.Add($"{name}: пустой результат");
        }
        return "ПОИСК НЕ УДАЛСЯ — все поисковики вернули ошибку:\n"
            + string.Join("\n", errors.Select(e => "  • " + e))
            + "\n\nВозможно, поисковики блокируются провайдером или рвёт TLS. "
            + "Предложи включить VPN или web_search (открыть браузер). Не повторяй тот же запрос.";
    }

    private static string FriendlyNetError(Exception ex)
    {
        string s = (ex.GetBaseException()?.Message ?? ex.Message).ToLowerInvariant();
        if (s.Contains("10054") || s.Contains("connection reset")) return "соединение разорвано";
        if (s.Contains("10060") || s.Contains("timed out") || s.Contains("timeout")) return "превышено время ожидания";
        if (s.Contains("10061") || s.Contains("refused")) return "соединение отклонено";
        if (s.Contains("11001") || s.Contains("getaddrinfo") || s.Contains("no such host")) return "не удалось разрешить DNS-имя";
        return ex.GetBaseException()?.Message ?? ex.Message;
    }
}
