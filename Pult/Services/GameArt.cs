using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pult.Services;

// Обложки игр: Steam-кэш (header/capsule/logo по всем трём раскладкам папок),
// иначе иконка exe → кэш PNG. Нулл — карточка рисует глиф.
public static class GameArt
{
    private static string CacheDir()
    {
        try
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pult", "gameart");
            Directory.CreateDirectory(d);
            return d;
        }
        catch { return ""; }
    }

    private static string? SteamArt(string appid)
    {
        try
        {
            string steam = GameService.SteamDir();
            if (steam.Length == 0) return null;
            string lib = Path.Combine(steam, "appcache", "librarycache");
            if (!Directory.Exists(lib)) return null;
            string[] cands =
            {
                Path.Combine(lib, appid, "header.jpg"),
                Path.Combine(lib, appid, "library_header.jpg"),
                Path.Combine(lib, appid + "_header.jpg"),
                Path.Combine(lib, appid, "library_capsule.jpg"),
                Path.Combine(lib, appid + "_library_capsule.jpg"),
                Path.Combine(lib, appid, "logo.png"),
                Path.Combine(lib, appid, "library_hero.jpg"),
            };
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; }
                catch { }
            }
            // Новая раскладка с хешем: appid/<hash>/*.jpg — первый header/capsule/logo.
            try
            {
                string sub = Path.Combine(lib, appid);
                if (Directory.Exists(sub))
                {
                    foreach (string f in Directory.EnumerateFiles(sub, "*.jpg", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string n = Path.GetFileName(f).ToLowerInvariant();
                            if (n.Contains("header") || n.Contains("capsule") || n.Contains("logo"))
                                return f;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        catch { }
        return null;
    }

    private static string ExeOf(Game g)
    {
        try
        {
            foreach (string cand in new[] { g.Launch, g.Path })
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(cand) && File.Exists(cand)) return cand;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    // Путь + баннер ли: Steam-арт — широкий баннер, иконка exe — квадрат в строку.
    public static (string? Path, bool Banner) GetDisplayArt(Game g)
    {
        try
        {
            if (g.Source == "Steam")
            {
                var m = Regex.Match(g.Launch ?? "", @"rungameid/(\d+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string? art = SteamArt(m.Groups[1].Value);
                    if (art != null) return (art, true);
                }
            }
            string exe = ExeOf(g);
            if (exe.Length > 0)
            {
                string? icon = GetIconPng(exe);
                if (icon != null) return (icon, false);
            }
        }
        catch { }
        return (null, false);
    }

    private static string? GetIconPng(string exe)
    {
        try
        {
            string dir = CacheDir();
            if (dir.Length == 0) return null;
            byte[] hash;
            try { hash = SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToLowerInvariant())); }
            catch { return null; }
            string file = Path.Combine(dir, Convert.ToHexString(hash)[..16] + ".png");
            if (File.Exists(file)) return file;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch { return null; }
    }
}
