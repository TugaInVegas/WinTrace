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
        public static string Write(string path, TraceMetadata meta, IEnumerable<TraceEvent> events, Func<string, byte[]?> resource)
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
