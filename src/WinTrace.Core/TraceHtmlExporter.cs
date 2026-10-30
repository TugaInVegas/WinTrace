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
        public static string Write(string path, TraceMetadata meta, IEnumerable<TraceEvent> events, Func<string, byte[]?> resource, IDictionary<string, string>? videoFiles = null)
        {
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var evs = events.OrderBy(e => e.Seq).ToList();
            var res = new Dictionary<string, string>();
            foreach (var e in evs)
                foreach (var id in new[] { e.ScreenshotBefore, e.ScreenshotAfter, e.Snapshot })
                {
                    if (id == null || res.ContainsKey(id)) continue;
                    var bytes = resource(id);
                    if (bytes == null) continue;
                    res[id] = id.EndsWith(".png")
                        ? "data:image/png;base64," + Convert.ToBase64String(bytes)
                        : Encoding.UTF8.GetString(bytes);
                }
            var stem = Path.GetFileNameWithoutExtension(path);
            var filesDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", stem + "_files");
            foreach (var v in meta.Videos)
            {
                if (videoFiles == null || !videoFiles.TryGetValue(v.File, out var src) || !File.Exists(src)) continue;
                Directory.CreateDirectory(filesDir);
                var dest = Path.Combine(filesDir, Path.GetFileName(v.File));
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, dest, true);
                v.Src = Uri.EscapeUriString(stem + "_files/" + Path.GetFileName(v.File));
            }
            var data = JsonSerializer.Serialize(new { meta, events = evs, resources = res }, opts)
                .Replace("</", "<\\/");

            string template;
            using (var s = typeof(TraceHtmlExporter).GetTypeInfo().Assembly.GetManifestResourceStream("viewer.html")!)
            using (var r = new StreamReader(s, Encoding.UTF8)) template = r.ReadToEnd();

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, template.Replace("/*__TRACE_DATA__*/null", data), new UTF8Encoding(false));
            return path;
        }
    }
}
