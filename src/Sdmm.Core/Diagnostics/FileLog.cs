using System.Text;

namespace Sdmm.Core.Diagnostics;

/// <summary>
/// SDMM's two log files, both next to the settings file:
/// <list type="bullet">
/// <item><b>crash.log</b>, always on: crashes (errors nothing else caught), with full details. At most
/// <see cref="CrashMaxBytes"/>.</item>
/// <item><b>sdmm.log</b>, the detailed log, off by default (Settings → Storage): everything SDMM does to the machine and
/// the network (file and folder changes in games and on servers, its own settings and cache files, SMM profile edits,
/// web requests, programs it starts) plus errors and crashes. At most <see cref="MaxBytes"/>.</item>
/// </list>
/// When a write takes a file over its limit, the oldest lines are dropped (first in, first out) so it shrinks to about
/// four fifths of the limit. Tokens and passwords are never written. Everything is best effort: a log that can't be
/// written never stops what SDMM is doing.
/// </summary>
public static class FileLog
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const long CrashMaxBytes = 1024 * 1024;
    public const string FileName = "sdmm.log";
    public const string CrashFileName = "crash.log";

    private static readonly object Gate = new();
    private static RollingFile? _detail;
    private static RollingFile? _crash;
    private static volatile bool _enabled;

    /// <summary>Whether the detailed log is on. Cheap to check before building a message.</summary>
    public static bool Enabled => _enabled;

    /// <summary>The detailed log file, once configured (whether or not it's on).</summary>
    public static string? Path => _detail?.Path;

    /// <summary>The crash log file, once configured.</summary>
    public static string? CrashPath => _crash?.Path;

    /// <summary>Puts both logs in <paramref name="folder"/> and turns the detailed one on or off. Safe to call again.</summary>
    public static void Configure(string folder, bool detailed)
    {
        lock (Gate)
        {
            var detailPath = System.IO.Path.Combine(folder, FileName);
            if (_detail?.Path != detailPath)
            {
                _detail?.Close();
                _crash?.Close();
                _detail = new RollingFile(detailPath, MaxBytes);
                _crash = new RollingFile(System.IO.Path.Combine(folder, CrashFileName), CrashMaxBytes);
            }
            bool wasOn = _enabled;
            _enabled = detailed;
            if (!detailed) _detail.Close();
            else if (!wasOn) _detail.Write("log", "Detailed logging on.");
        }
    }

    /// <summary>One line in the detailed log: time, area (for example "fs", "http", "app") and message.</summary>
    public static void Write(string area, string message)
    {
        if (!_enabled) return;
        lock (Gate) _detail?.Write(area, message);
    }

    /// <summary>An error with its type, message, inner errors and stack trace, in the detailed log.</summary>
    public static void WriteError(string area, string context, Exception ex) => Write(area, $"{context}: {ex}");

    /// <summary>A crash: always written to crash.log, and to the detailed log too when that's on.</summary>
    public static void WriteCrash(string context, Exception ex)
    {
        lock (Gate)
        {
            _crash?.Write("crash", $"{context}: {ex}");
            if (_enabled) _detail?.Write("crash", $"{context}: {ex}");
        }
    }

    /// <summary>A line in crash.log that isn't a crash itself (the version at startup, so a report says what crashed).</summary>
    public static void WriteCrashContext(string message)
    {
        lock (Gate) _crash?.Write("start", message);
    }

    /// <summary>Closes both files (at exit).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _detail?.Close();
            _crash?.Close();
        }
    }

    /// <summary>An append-only text file that drops its oldest lines when it grows past its limit.</summary>
    private sealed class RollingFile(string path, long maxBytes)
    {
        private StreamWriter? _writer;
        public string Path { get; } = path;

        public void Write(string area, string message)
        {
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                }
                // One entry per line (continuation lines are indented), so a trim never cuts an entry's first line in half.
                var text = message.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n    ");
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{area}] {text}");
                if (_writer.BaseStream.Length > maxBytes) Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Close(); // try again on the next line; a broken log must never break SDMM
            }
        }

        public void Close()
        {
            try { _writer?.Dispose(); }
            catch (IOException) { }
            _writer = null;
        }

        /// <summary>Keeps the newest four fifths of the limit, from the first whole line, and drops the rest.</summary>
        private void Trim()
        {
            Close();
            byte[] tail;
            using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long keep = Math.Min(maxBytes * 4 / 5, f.Length);
                f.Seek(-keep, SeekOrigin.End);
                tail = new byte[keep];
                f.ReadExactly(tail);
            }
            int start = Array.IndexOf(tail, (byte)'\n') + 1;
            var tmp = Path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                f.Write(Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [log] Older lines were dropped to keep this file under {maxBytes / 1024} KB.\n"));
                f.Write(tail, start, tail.Length - start);
            }
            File.Move(tmp, Path, overwrite: true);
        }
    }
}
