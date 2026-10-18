namespace Phosphor.App;

/// <summary>Лог в %TEMP%\phosphor.log (у WinExe нет консоли) + дублирование в stdout, если она есть.</summary>
internal sealed class Log : IDisposable
{
    private readonly StreamWriter _w;
    public string Path { get; }

    public Log()
    {
        // Несколько экземпляров одновременно (превью + /s, параллельные --shot) не должны ронять друг друга:
        // если общий лог занят — пишем в свой phosphor-<pid>.log.
        var tmp = System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(tmp, "phosphor.log");
        try
        {
            _w = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Свой файл по PID; если и он занят (второй Log в том же процессе) — с номером
            for (int n = 0; ; n++)
            {
                Path = System.IO.Path.Combine(tmp, n == 0 ? $"phosphor-{Environment.ProcessId}.log" : $"phosphor-{Environment.ProcessId}-{n}.log");
                try
                {
                    _w = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    break;
                }
                catch (IOException) when (n < 16) { }
            }
        }
        LogPrune.Run(tmp, DateTime.UtcNow, Path);
    }

    public void Info(string m) => Write("INFO", m);
    public void Warn(string m) => Write("WARN", m);
    public void Error(string m) => Write("ERR ", m);

    private void Write(string lvl, string m)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {lvl} {m}";
        _w.WriteLine(line);
        try { Console.WriteLine(line); } catch { /* нет консоли */ }
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>
/// Подчистка %TEMP% за прошлыми запусками: phosphor-&lt;pid&gt;[-n].log и phosphor-preview-&lt;pid&gt;.json старше трёх суток
/// (их сотни: превью и /s часто стартуют одновременно). Свой лог и занятые файлы не трогаются.
/// </summary>
public static class LogPrune
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private static readonly System.Text.RegularExpressions.Regex Name =
        new(@"^phosphor-(\d+(-\d+)?\.log|preview-\d+\.json)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsOurs(string fileName) => Name.IsMatch(fileName);

    public static int Run(string dir, DateTime nowUtc, string? keep)
    {
        int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "phosphor-*"))
            {
                if (!IsOurs(System.IO.Path.GetFileName(f))) continue;
                if (keep is not null && string.Equals(System.IO.Path.GetFullPath(f), System.IO.Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(f) < MaxAge) continue;
                    File.Delete(f);
                    n++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return n;
    }
}
