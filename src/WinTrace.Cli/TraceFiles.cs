using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinTrace.Cli
{
    /// <summary>Finds trace zips written by TraceSession (&lt;test&gt;_yyyyMMdd_HHmmss[_failed].zip) under a folder.</summary>
    public static class TraceFiles
    {
        static readonly Regex TraceName = new Regex(@"_\d{8}_\d{6}(_failed)?\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Trace zips under <paramref name="folder"/> (recursively), newest first.</summary>
        public static IReadOnlyList<FileInfo> Find(string folder)
        {
            if (!Directory.Exists(folder)) return Array.Empty<FileInfo>();
            var found = new List<FileInfo>();
            foreach (var path in SafeEnumerate(folder))
                if (TraceName.IsMatch(path)) found.Add(new FileInfo(path));
            return found.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }

        public static bool IsFailedName(string path) => path.EndsWith("_failed.zip", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A trace file as given, or for a folder the newest failed trace in it (else the newest trace).
        /// Returns null when nothing is found; <paramref name="picked"/> says which file was chosen from a folder.
        /// </summary>
        public static string? Resolve(string pathOrFolder, out string? picked)
        {
            picked = null;
            if (File.Exists(pathOrFolder)) return Path.GetFullPath(pathOrFolder);
            var all = Find(pathOrFolder);
            var chosen = all.FirstOrDefault(f => IsFailedName(f.Name)) ?? all.FirstOrDefault();
            if (chosen == null) return null;
            picked = chosen.FullName;
            return chosen.FullName;
        }

        // Skips folders that cannot be read instead of failing the whole search.
        static IEnumerable<string> SafeEnumerate(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] files, dirs;
                try { files = Directory.GetFiles(dir, "*.zip"); dirs = Directory.GetDirectories(dir); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in dirs)
                    if (!Path.GetFileName(d).StartsWith(".")) pending.Push(d);
            }
        }
    }
}
