using System;
using System.IO;

namespace Pult.Services;

public static class CrashLog
{
    public static string Path
    {
        get
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "crash.log");
            }
            catch { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pult-crash.log"); }
        }
    }

    public static void Write(string where, Exception? ex)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n---\n";
        // Пишем сразу в 3 места — какое-то точно откроется через Win+R.
        try { File.AppendAllText(Path, line); } catch { }
        try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pult-crash.log"), line); } catch { }
        try
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            File.AppendAllText(System.IO.Path.Combine(exeDir, "crash.log"), line);
        }
        catch { }
    }

    public static void Trace(string what)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {what}\n";
        try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pult-trace.log"), line); } catch { }
        try
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            File.AppendAllText(System.IO.Path.Combine(exeDir, "trace.log"), line);
        }
        catch { }
    }
}
