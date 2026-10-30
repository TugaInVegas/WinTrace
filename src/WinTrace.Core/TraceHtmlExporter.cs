using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    public static class TraceHtmlExporter
    {
        /// <summary>Writes the HTML report and returns its path. See <see cref="Export(string, TraceMetadata, IEnumerable{TraceEvent}, Func{string, byte[]}, IDictionary{string, string}, bool, bool)"/>.</summary>
        public static string Write(string path, TraceMetadata meta, IEnumerable<TraceEvent> events, Func<string, byte[]?> resource,
            IDictionary<string, string>? videoFiles = null, bool embedVideo = true) =>
            Export(path, meta, events, resource, videoFiles, embedVideo, true)[0];

        /// <summary>
        /// Writes the HTML report. With <paramref name="embedVideo"/> (default) videos go inside the HTML, so the one file
        /// is enough (e.g. as a CI attachment). Otherwise they are copied to <c>&lt;name&gt;_files/</c> next to it.
        /// Returns every file written, the HTML first.
        /// </summary>
        public static IReadOnlyList<string> Export(string path, TraceMetadata meta, IEnumerable<TraceEvent> events, Func<string, byte[]?> resource,
            IDictionary<string, string>? videoFiles = null, bool embedVideo = true) =>
            Export(path, meta, events, resource, videoFiles, embedVideo, true);

        /// <summary>
        /// As <see cref="Export(string, TraceMetadata, IEnumerable{TraceEvent}, Func{string, byte[]}, IDictionary{string, string}, bool)"/>;
        /// Playwright traces merged into the trace are embedded too (default) or copied next to the HTML, so the viewer can
        /// hand them to Playwright's trace viewer.
        /// </summary>
        public static IReadOnlyList<string> Export(string path, TraceMetadata meta, IEnumerable<TraceEvent> events, Func<string, byte[]?> resource,
            IDictionary<string, string>? videoFiles, bool embedVideo, bool embedPlaywrightTraces)
        {
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var evs = events.OrderBy(e => e.Seq).ToList();
            var written = new List<string> { path };
            var res = new Dictionary<string, string>();
            foreach (var e in evs)
                foreach (var id in new[] { e.ScreenshotBefore, e.ScreenshotAfter, e.Snapshot })
                {
                    if (id == null || res.ContainsKey(id)) continue;
                    var bytes = resource(id);
                    if (bytes == null) continue;
                    res[id] = id.EndsWith(".png") ? "data:image/png;base64," + Convert.ToBase64String(bytes)
                        : id.EndsWith(".jpg") ? "data:image/jpeg;base64," + Convert.ToBase64String(bytes)
                        : Encoding.UTF8.GetString(bytes);
                }

            var stem = Path.GetFileNameWithoutExtension(path);
            var filesDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", stem + "_files");
            foreach (var v in meta.Videos)
            {
                if (videoFiles == null || !videoFiles.TryGetValue(v.File, out var src) || !File.Exists(src)) continue;
                if (embedVideo)
                {
                    // Raw base64; the viewer turns it into a Blob URL, which is also seekable everywhere.
                    res[v.File] = Convert.ToBase64String(File.ReadAllBytes(src));
                    v.Embedded = v.File;
                    v.Src = null;
                    continue;
                }
                Directory.CreateDirectory(filesDir);
                var dest = Path.Combine(filesDir, Path.GetFileName(v.File));
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, dest, true);
                v.Src = Uri.EscapeUriString(stem + "_files/" + Path.GetFileName(v.File));
                written.Add(dest);
            }
            foreach (var p in meta.PlaywrightTraces)
            {
                var bytes = resource(p.File);
                if (bytes == null) continue;
                if (embedPlaywrightTraces)
                {
                    res[p.File] = Convert.ToBase64String(bytes);
                    p.Embedded = p.File;
                    p.Src = null;
                    continue;
                }
                Directory.CreateDirectory(filesDir);
                var dest = Path.Combine(filesDir, Path.GetFileName(p.File));
                File.WriteAllBytes(dest, bytes);
                p.Src = Uri.EscapeUriString(stem + "_files/" + Path.GetFileName(p.File));
                written.Add(dest);
            }
            var data = JsonSerializer.Serialize(new { meta, events = evs, resources = res }, opts)
                .Replace("</", "<\\/");

            string template;
            using (var s = typeof(TraceHtmlExporter).GetTypeInfo().Assembly.GetManifestResourceStream("viewer.html")!)
            using (var r = new StreamReader(s, Encoding.UTF8)) template = r.ReadToEnd();

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, template.Replace("/*__TRACE_DATA__*/null", data), new UTF8Encoding(false));
            return written;
        }
    }
}
