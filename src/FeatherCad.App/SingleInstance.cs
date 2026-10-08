using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace FeatherCad.App;

/// <summary>
/// Opening a file (Explorer double-click, Open with) while SpryCAD runs: the new process hands the paths to the
/// running one over a named pipe and exits, and they open there in tabs. The pipe is per user and per logon session
/// and only accepts the same user. Started without files, SpryCAD always opens a window of its own.
/// </summary>
static class SingleInstance
{
    static readonly string PipeName = $"SpryCAD-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>Sends the files to a running SpryCAD; false when none is running (or it does not answer).</summary>
    public static bool TrySend(string[] files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(400);
            // Windows lets only the foreground process bring a window forward: allow the running one to
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            var data = Encoding.UTF8.GetBytes(string.Join("\n", files.Select(Path.GetFullPath)));
            pipe.Write(data, 0, data.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception) { return false; } // nobody listening (timeout), or the pipe broke: open here instead
    }

    /// <summary>Accepts files from later launches for as long as the app runs, calling <paramref name="open"/> on a pool thread.</summary>
    public static void Listen(Action<string[]> open)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    using var ms = new MemoryStream();
                    await pipe.CopyToAsync(ms);
                    var files = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (files.Length > 0) open(files);
                }
                catch (Exception)
                {
                    await Task.Delay(1000); // e.g. a client that hung up early; keep serving
                }
            }
        });
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
