using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>Loads a trace.zip written by <see cref="TraceRecorder.Save"/>.</summary>
    public sealed class LoadedTrace
    {
        public TraceMetadata Meta { get; set; } = new TraceMetadata();
        public List<TraceEvent> Events { get; set; } = new List<TraceEvent>();
        public Dictionary<string, byte[]> Resources { get; set; } = new Dictionary<string, byte[]>();
        /// <summary>zip entry name -> extracted file on disk.</summary>
        public Dictionary<string, string> VideoFiles { get; set; } = new Dictionary<string, string>();
    }

    public static class TraceReader
    {
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static LoadedTrace Read(string zipPath, string videoExtractDir)
        {
            var t = new LoadedTrace();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries.Where(e => e.Length > 0))
                {
                    if (e.FullName == "trace.json")
                        t.Meta = JsonSerializer.Deserialize<TraceMetadata>(Text(e), Json) ?? t.Meta;
                    else if (e.FullName == "events.ndjson")
                        t.Events = Text(e).Split('\n').Where(l => l.Length > 0)
                            .Select(l => JsonSerializer.Deserialize<TraceEvent>(l, Json)!).ToList();
                    else if (e.FullName.StartsWith("video/"))
                    {
                        Directory.CreateDirectory(videoExtractDir);
                        var dest = Path.Combine(videoExtractDir, Path.GetFileName(e.FullName));
                        using (var s = e.Open()) using (var f = File.Create(dest)) s.CopyTo(f);
                        t.VideoFiles[e.FullName] = dest;
                    }
                    else
                        using (var s = e.Open()) using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            t.Resources[e.FullName] = ms.ToArray();
                        }
                }
            }
            return t;
        }

        static string Text(ZipArchiveEntry e)
        {
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }
    }
}
