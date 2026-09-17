using System.Text;

namespace SoundpadHUD;

internal static class SelfTest
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("--- raw pipe diagnostics ---");
            try
            {
                using (var p = new System.IO.Pipes.NamedPipeClientStream(".", "sp_remote_control", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    p.Connect(1500);
                    sb.AppendLine("raw connect: OK  IsConnected=" + p.IsConnected + " CanRead=" + p.CanRead + " CanWrite=" + p.CanWrite);
                    var bytes = Encoding.UTF8.GetBytes("GetVersion()");
                    p.Write(bytes, 0, bytes.Length);
                    p.Flush();
                    var buf = new byte[4096];
                    var t = p.ReadAsync(buf, 0, buf.Length);
                    bool done = t.Wait(2000);
                    sb.AppendLine("raw read: done=" + done + (done ? (" n=" + t.Result + " text=[" + Encoding.UTF8.GetString(buf, 0, Math.Max(0, t.Result)) + "]") : ""));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("raw pipe FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
            sb.AppendLine("--- SoundpadClient ---");

            using var c = new SoundpadClient();
            sb.AppendLine("IsAlive()                = " + c.Send("IsAlive()"));
            sb.AppendLine("GetVersion()             = " + c.Send("GetVersion()"));
            sb.AppendLine("GetRemoteControlVersion  = " + c.Send("GetRemoteControlVersion()"));
            sb.AppendLine("GetTitleText()           = " + c.Send("GetTitleText()"));
            sb.AppendLine("GetStatusBarText()       = " + c.Send("GetStatusBarText()"));
            sb.AppendLine("GetPlayStatus()          = " + c.Send("GetPlayStatus()"));
            sb.AppendLine("GetPlaybackDurationInMs  = " + c.Send("GetPlaybackDurationInMs()"));
            sb.AppendLine("GetSoundFileCount()      = " + c.Send("GetSoundFileCount()"));
            sb.AppendLine();

            var cats = c.GetCategories();
            sb.AppendLine("categories: " + cats.Count);
            foreach (var cat in cats)
                sb.AppendLine("  idx=" + cat.Index + " type=" + cat.Type + " hidden=" + cat.Hidden + " name=" + cat.Name);

            var all = cats.FirstOrDefault(x => x.Type == 1) ?? cats.FirstOrDefault(x => x.Hidden);
            if (all != null)
                sb.AppendLine("DoSelectCategory(" + all.Index + ") = " + c.SelectCategory(all.Index));
            sb.AppendLine();

            var sounds = c.GetSoundList();
            sb.AppendLine("sounds: " + sounds.Count);
            foreach (var s in sounds)
                sb.AppendLine("  #" + s.Index + "  " + s.Duration + "  plays=" + s.PlayCount + "  " + s.Title);
            sb.AppendLine();
            sb.AppendLine("DoSelectIndex 合法范围探测 (0 起):");
            foreach (int i in new[] { -1, 0, sounds.Count - 1, sounds.Count })
                sb.AppendLine("  DoSelectIndex(" + i + ") = " + c.SelectIndex(i));
            sb.AppendLine("DoPlaySound 索引探测 (speakers=false, mic=false，不发声):");
            foreach (int i in new[] { 0, 1, sounds.Count, sounds.Count + 1 })
                sb.AppendLine("  DoPlaySound(" + i + ", false, false) = " + c.Send("DoPlaySound(" + i + ", false, false)"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }

        string file = Path.Combine(AppContext.BaseDirectory, "selftest.txt");
        try { File.WriteAllText(file, sb.ToString()); } catch { }
        try { Console.WriteLine(sb.ToString()); } catch { }
    }
}
