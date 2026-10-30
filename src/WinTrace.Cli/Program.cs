using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using WinTrace;

const string Usage = @"wintrace - inspect WinTrace traces

  wintrace show <trace.zip> [--out <file.html>] [--no-open]   Build an HTML report and open it
  wintrace summary <trace.zip>                                 Print steps and errors (exit code 1 if any failed)
  wintrace --version";

if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
if (args[0] == "--version")
{
    var info = typeof(TraceRecorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
    Console.WriteLine(info.Split('+')[0]);
    return 0;
}

if (args.Length < 2 || !File.Exists(args[1])) { Console.Error.WriteLine("Trace file not found.\n\n" + Usage); return 2; }
var zip = Path.GetFullPath(args[1]);

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

try
{
    switch (args[0])
    {
        case "show":
            return Show();
        case "summary":
            return Summary();
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidDataException || ex is System.Text.Json.JsonException || ex is IOException)
{
    Console.Error.WriteLine($"Could not read '{zip}' as a WinTrace trace: {ex.Message}");
    return 2;
}

int Show()
{
    var outHtml = Path.GetFullPath(Opt("--out") ?? Path.ChangeExtension(zip, ".html"));
    var stem = Path.GetFileNameWithoutExtension(outHtml);
    var filesDir = Path.Combine(Path.GetDirectoryName(outHtml)!, stem + "_files");
    var t = TraceReader.Read(zip, filesDir);
    TraceHtmlExporter.Write(outHtml, t.Meta, t.Events, id => t.Resources.TryGetValue(id, out var b) ? b : null, t.VideoFiles);
    Console.WriteLine(outHtml);
    if (!args.Contains("--no-open"))
        try { Process.Start(new ProcessStartInfo(outHtml) { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine("Could not open browser: " + ex.Message); }
    return 0;
}

int Summary()
{
    var tmp = Path.Combine(Path.GetTempPath(), "wintrace-" + Guid.NewGuid().ToString("N"));
    try
    {
        var t = TraceReader.Read(zip, tmp);
        Console.WriteLine($"{t.Meta.Title}  ({t.Events.Count} events, {t.Events.Count(e => e.Kind == "error")} errors)");
        foreach (var e in t.Events)
        {
            var off = (e.Start - t.Meta.Started).TotalSeconds;
            Console.WriteLine($"{(e.Kind == "error" ? "FAIL" : e.Kind == "log" ? "log " : "ok  ")} +{off,7:F2}s {e.DurationMs,7:F0}ms  {e.Group ?? "-"} | {e.Name} {e.Target}");
            if (e.Error != null) Console.WriteLine("       " + e.Error);
        }
        return t.Events.Any(e => e.Kind == "error") ? 1 : 0;
    }
    finally
    {
        // summary only reads events; drop any video extracted from the zip.
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
}
