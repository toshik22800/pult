using System;
using System.Collections.Generic;
using System.IO;

namespace Pult.Services;

// Файловые операции. Порт tools/files.py — в разы короче за счёт BCL.
public static class FileService
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "Документы", [".docx"] = "Документы", [".doc"] = "Документы",
        [".txt"] = "Документы", [".xlsx"] = "Документы", [".xls"] = "Документы",
        [".pptx"] = "Документы", [".odt"] = "Документы", [".csv"] = "Документы",
        [".jpg"] = "Картинки", [".jpeg"] = "Картинки", [".png"] = "Картинки",
        [".gif"] = "Картинки", [".bmp"] = "Картинки", [".svg"] = "Картинки",
        [".webp"] = "Картинки",
        [".mp4"] = "Видео", [".mkv"] = "Видео", [".avi"] = "Видео",
        [".mov"] = "Видео", [".webm"] = "Видео",
        [".mp3"] = "Музыка", [".wav"] = "Музыка", [".flac"] = "Музыка",
        [".aac"] = "Музыка",
        [".zip"] = "Архивы", [".rar"] = "Архивы", [".7z"] = "Архивы",
        [".tar"] = "Архивы", [".gz"] = "Архивы",
        [".exe"] = "Программы", [".msi"] = "Программы", [".lnk"] = "Программы",
        [".iso"] = "Программы",
    };

    public static string OrganizeDirectory(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(directory));
            if (!dir.Exists) return $"Ошибка: папка {directory} не найдена.";

            int moved = 0;
            var errors = new List<string>();
            foreach (var file in dir.GetFiles())
            {
                string cat = Categories.TryGetValue(file.Extension, out var c) ? c : "Другое";
                var targetDir = Directory.CreateDirectory(Path.Combine(dir.FullName, cat));
                string dest = Path.Combine(targetDir.FullName, file.Name);
                if (File.Exists(dest))
                    dest = Path.Combine(targetDir.FullName,
                        $"{Path.GetFileNameWithoutExtension(file.Name)}_copy{file.Extension}");
                try
                {
                    file.MoveTo(dest);
                    moved++;
                }
                catch (Exception ex) { errors.Add($"{file.Name}: {ex.Message}"); }
            }

            string res = $"Порядок наведён! Раскидано файлов: {moved} в папке {dir.FullName}";
            if (errors.Count > 0)
                res += "\nНе удалось:\n  • " + string.Join("\n  • ", errors.GetRange(0, Math.Min(5, errors.Count)));
            return res;
        }
        catch (Exception ex)
        {
            return $"Ошибка при сортировке: {ex.Message}";
        }
    }

    public static string Downloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
