using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

namespace SoundpadHUD;

public sealed class SoundInfo
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Duration { get; set; } = "";
    public int PlayCount { get; set; }
    public double DurationSeconds { get; set; }
}

public sealed class CategoryInfo
{
    public int Index { get; set; }
    public int Type { get; set; }
    public string Name { get; set; } = "";
    public bool Hidden { get; set; }
}

public enum PlayStatus { Unknown, Stopped, Playing, Paused, Seeking }

/// <summary>
/// Soundpad 官方远程控制客户端（命名管道 \\.\pipe\sp_remote_control）。
/// 参考官方 SoundpadRemoteControl.java (RC 1.1.2)。
/// </summary>
public sealed class SoundpadClient : IDisposable
{
    private const string PipeName = "sp_remote_control";
    private readonly object _gate = new object();
    private NamedPipeClientStream _pipe;
    private long _lastMs;

    public bool Connected => _pipe != null && _pipe.IsConnected;

    public void Reset()
    {
        lock (_gate) { DisposePipe(); }
    }

    private void DisposePipe()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public string Send(string command, int firstByteTimeoutMs = 900)
    {
        lock (_gate)
        {
            if (!EnsureConnected()) return "";
            try
            {
                long now = Environment.TickCount64;
                if (now - _lastMs < 5) Thread.Sleep((int)(5 - (now - _lastMs)));
                var data = Encoding.UTF8.GetBytes(command);
                _pipe.Write(data, 0, data.Length);
                _pipe.Flush();
                string resp = ReadResponse(firstByteTimeoutMs);
                _lastMs = Environment.TickCount64;
                return resp;
            }
            catch
            {
                DisposePipe();
                return "";
            }
        }
    }

    private bool EnsureConnected()
    {
        if (_pipe != null && _pipe.IsConnected) return true;
        DisposePipe();
        try
        {
            var p = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            p.Connect(1200);
            _pipe = p;
            _lastMs = 0;
            return true;
        }
        catch
        {
            _pipe = null;
            return false;
        }
    }

    private string ReadResponse(int firstTimeoutMs)
    {
        var ms = new MemoryStream();
        var buf = new byte[8192];
        long start = Environment.TickCount64;
        while (true)
        {
            int wait = ms.Length == 0 ? firstTimeoutMs : 80;
            int n;
            using (var cts = new CancellationTokenSource(wait))
            {
                Task<int> task;
                try { task = _pipe.ReadAsync(buf, 0, buf.Length, cts.Token); }
                catch { break; }
                try
                {
                    task.Wait();
                    if (!task.IsCompletedSuccessfully) break;
                    n = task.Result;
                }
                catch { break; }
            }
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            if (Environment.TickCount64 - start > 4000) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static bool Ok(string resp) => resp != null && resp.StartsWith("R-200", StringComparison.Ordinal);

    public string GetVersion()
    {
        var r = Send("GetVersion()");
        return r.StartsWith("R-") ? "" : r.Trim();
    }

    public bool IsAlive() => Ok(Send("IsAlive()"));

    public PlayStatus GetPlayStatus()
    {
        var r = Send("GetPlayStatus()").Trim().ToUpperInvariant();
        switch (r)
        {
            case "PLAYING": return PlayStatus.Playing;
            case "PAUSED": return PlayStatus.Paused;
            case "SEEKING": return PlayStatus.Seeking;
            case "STOPPED": return PlayStatus.Stopped;
            default: return PlayStatus.Unknown;
        }
    }

    public long GetPlaybackPositionMs() => ParseLong(Send("GetPlaybackPositionInMs()"));
    public long GetPlaybackDurationMs() => ParseLong(Send("GetPlaybackDurationInMs()"));

    private static long ParseLong(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || s.StartsWith("R-")) return -1;
        return long.TryParse(s, out long v) ? v : -1;
    }

    public List<SoundInfo> GetSoundList()
    {
        var list = new List<SoundInfo>();
        var xml = Send("GetSoundlist()", 1600);
        xml = xml ?? "";
        int lt = xml.IndexOf('<');
        if (lt < 0) return list;
        if (lt > 0) xml = xml.Substring(lt);
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Descendants("Sound"))
            {
                string dur = (string)e.Attribute("duration") ?? "";
                list.Add(new SoundInfo
                {
                    Index = (int?)e.Attribute("index") ?? 0,
                    Title = (string)e.Attribute("title") ?? "",
                    Url = (string)e.Attribute("url") ?? "",
                    Duration = dur,
                    PlayCount = (int?)e.Attribute("playCount") ?? 0,
                    DurationSeconds = ParseDuration(dur),
                });
            }
        }
        catch { }
        return list;
    }

    public List<CategoryInfo> GetCategories()
    {
        var list = new List<CategoryInfo>();
        var xml = Send("GetCategories(false, false)", 1600) ?? "";
        int lt = xml.IndexOf('<');
        if (lt < 0) return list;
        if (lt > 0) xml = xml.Substring(lt);
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Descendants("Category"))
            {
                list.Add(new CategoryInfo
                {
                    Index = (int?)e.Attribute("index") ?? 0,
                    Type = (int?)e.Attribute("type") ?? 0,
                    Name = (string)e.Attribute("name") ?? "",
                    Hidden = string.Equals((string)e.Attribute("hidden"), "true", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch { }
        return list;
    }

    public static double ParseDuration(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, out double v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    public static string FormatMs(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
    }

    public string SelectIndex(int row) => Send("DoSelectIndex(" + row + ")");
    public string SelectCategory(int idx) => Send("DoSelectCategory(" + idx + ")");
    public string PlaySelected() => Send("DoPlaySelectedSound()");
    public string Play(int index) => Send("DoPlaySound(" + index + ")");
    public string Stop() => Send("DoStopSound()");

    public void Dispose() { lock (_gate) { DisposePipe(); } }
}
