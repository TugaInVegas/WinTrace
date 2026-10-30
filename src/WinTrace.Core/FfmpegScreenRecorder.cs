using System;
using System.Diagnostics;
using System.IO;

namespace WinTrace
{
    /// <summary>
    /// Minimal ffmpeg desktop recorder (Windows gdigrab). Already have a recorder? Skip this and call
    /// <see cref="TraceRecorder.AttachVideo"/> with your file and its start time instead.
    /// </summary>
    public sealed class FfmpegScreenRecorder : IDisposable
    {
        Process? _proc;
        public string OutputPath { get; }
        public DateTimeOffset StartedAt { get; private set; }

        FfmpegScreenRecorder(string output) { OutputPath = output; }

        public static string BuildArguments(string output, int fps = 10) =>
            $"-y -f gdigrab -framerate {fps} -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart \"{output}\"";

        public static FfmpegScreenRecorder Start(string outputPath, string ffmpegPath = "ffmpeg", int fps = 10)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var r = new FfmpegScreenRecorder(outputPath);
            r._proc = Process.Start(new ProcessStartInfo(ffmpegPath, BuildArguments(outputPath, fps))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            r.StartedAt = DateTimeOffset.UtcNow;
            // ffmpeg writes progress to stderr; drain it so the pipe never blocks the encoder.
            r._proc!.ErrorDataReceived += (_, __) => { };
            r._proc.BeginErrorReadLine();
            return r;
        }

        /// <summary>Gracefully stops ffmpeg ('q') so the mp4 is finalized and playable.</summary>
        public void Stop(int timeoutMs = 10000)
        {
            var p = _proc;
            if (p == null) return;
            _proc = null;
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Write("q");
                    p.StandardInput.Flush();
                    if (!p.WaitForExit(timeoutMs)) p.Kill();
                }
            }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }

        public void Dispose() => Stop();
    }
}
