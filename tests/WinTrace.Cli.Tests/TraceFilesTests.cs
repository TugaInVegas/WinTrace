using System;
using System.IO;
using System.Linq;
using System.Threading;
using WinTrace.Cli;
using Xunit;

public class TraceFilesTests
{
    [Fact]
    public void Folder_resolves_to_newest_failed_trace_and_ignores_other_zips()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintrace-files", Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "bin", "Debug", "TestResults", "WinTrace");
        Directory.CreateDirectory(sub);
        void Touch(string name, int minutesAgo)
        {
            var p = Path.Combine(sub, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-minutesAgo));
        }
        Touch("Login_20261030_100000_failed.zip", 30);
        Touch("Submit_20261030_101000_failed.zip", 20);
        Touch("Submit_20261030_102000.zip", 10);       // newer, but passed
        Touch("unrelated.zip", 1);

        Assert.Equal(3, TraceFiles.Find(root).Count);
        Assert.EndsWith("Submit_20261030_101000_failed.zip", TraceFiles.Resolve(root, out var picked));
        Assert.NotNull(picked);
        var file = Path.Combine(sub, "Login_20261030_100000_failed.zip");
        Assert.Equal(Path.GetFullPath(file), TraceFiles.Resolve(file, out var none));
        Assert.Null(none);
        Assert.Null(TraceFiles.Resolve(Path.Combine(root, "missing"), out _));
    }
}
