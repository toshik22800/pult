using System;
using System.Diagnostics;
using System.Text;

namespace Pult.Services;

// Единственный способ запускать внешние процессы:
// асинхронное чтение ОБОИХ потоков (дедлоков нет),
// убийство по таймауту, OEM-кодировка для русского вывода.
public static class Proc
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    public static Result Run(string exe, string args, int timeoutMs = 30000, Encoding? enc = null)
    {
        enc ??= SystemMonitor.Oem();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            if (!p.Start()) return new Result(-1, "", "Не запустился.", false);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(5000); } catch { }
                return new Result(-1, stdout.ToString(), stderr.ToString(), true);
            }
            try { p.WaitForExit(); } catch { }
            return new Result(p.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (Exception ex)
        {
            return new Result(-1, stdout.ToString(), ex.Message, false);
        }
    }

}
