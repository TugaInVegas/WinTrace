using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>Writes the trace.zip format (see docs/trace-format.md); used by the recorder and by tools that combine traces.</summary>
    public static class TraceArchive
    {
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>Writes a trace.zip. <c>videoFiles</c> maps a zip entry name (e.g. video/screen.mp4) to the file on disk.</summary>
        public static string Write(string path, TraceMetadata meta, IEnumerable<TraceEvent> events,
            IEnumerable<KeyValuePair<string, byte[]>> resources, IDictionary<string, string>? videoFiles = null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "trace.json", JsonSerializer.Serialize(meta, Json));
                var sb = new StringBuilder();
                foreach (var e in events.OrderBy(e => e.Seq)) sb.Append(JsonSerializer.Serialize(e, Json)).Append('\n');
                WriteText(zip, "events.ndjson", sb.ToString());
                foreach (var kv in resources)
                {
                    // Images and zips are already compressed; deflating them again only costs time.
                    var stored = kv.Key.EndsWith(".png") || kv.Key.EndsWith(".jpg") || kv.Key.EndsWith(".zip");
                    var entry = zip.CreateEntry(kv.Key, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using (var s = entry.Open()) s.Write(kv.Value, 0, kv.Value.Length);
                }
                if (videoFiles != null)
                    foreach (var kv in videoFiles.Where(v => File.Exists(v.Value)))
                    {
                        var entry = zip.CreateEntry(kv.Key, CompressionLevel.NoCompression);
                        using (var s = entry.Open())
                        using (var f = new FileStream(kv.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            f.CopyTo(s);
                    }
            }
            return path;
        }

        static void WriteText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(text);
        }
    }
}
