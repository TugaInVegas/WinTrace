using System.Linq;
using WinTrace;
using Xunit;

public class TraceAnalysisTests
{
    const string Tree =
        "<Window Name=\"Sample App\" AutomationId=\"MainWindow\" x=\"0\" y=\"0\" width=\"800\" height=\"500\">" +
        "<ToolBar AutomationId=\"toolbar\"><Button Name=\"Submit\" AutomationId=\"btnSubmit\" ClassName=\"Button\"/>" +
        "<Button Name=\"Save\" AutomationId=\"btnSave\"/></ToolBar><Edit Name=\"Customer name\"/></Window>";

    [Theory]
    [InlineData("By.AccessibilityId: btnSubmitt", "btnSubmitt")]
    [InlineData("accessibility id=btnSubmitt", "btnSubmitt")]
    [InlineData("By.Name: Customer name", "Customer name")]
    [InlineData("btnSave", "btnSave")]
    [InlineData(null, "")]
    public void LocatorValue_extracts_the_value(string? target, string expected) =>
        Assert.Equal(expected, TraceAnalysis.LocatorValue(target));

    [Fact]
    public void FindSimilar_spots_a_typo_first()
    {
        var best = TraceAnalysis.FindSimilar(Tree, "By.AccessibilityId: btnSubmitt").First();
        Assert.Equal("btnSubmit", best.Element.AutomationId);
        Assert.Equal("MobileBy.AccessibilityId(\"btnSubmit\")", best.Element.SuggestedLocator());
        Assert.True(best.Score > 0.8);
    }

    [Fact]
    public void FindSimilar_matches_names_too()
    {
        var best = TraceAnalysis.FindSimilar(Tree, "By.Name: Customer nme").First();
        Assert.Equal("Customer name", best.Element.Name);
        Assert.Equal("By.Name(\"Customer name\")", best.Element.SuggestedLocator());
    }

    [Fact]
    public void FindSimilar_returns_nothing_for_unrelated_locators() =>
        Assert.Empty(TraceAnalysis.FindSimilar(Tree, "By.Name: Delete account forever"));

    [Fact]
    public void CompactTree_indents_by_depth_and_reads_bounds()
    {
        var lines = TraceAnalysis.CompactTree(Tree).Split('\n');
        Assert.StartsWith("Window Name=\"Sample App\"", lines[0]);
        Assert.StartsWith("    Button Name=\"Submit\"", lines[2]);
        Assert.Equal(800, TraceAnalysis.ParseTree(Tree)[0].Width);
    }

    [Fact]
    public void Non_xml_snapshots_are_returned_as_is()
    {
        Assert.Empty(TraceAnalysis.ParseTree("{\"json\":true}"));
        Assert.Equal("plain text", TraceAnalysis.CompactTree("plain text"));
        Assert.Empty(TraceAnalysis.ParseTree("<broken"));
    }
}

public class GuessLocatorTests
{
    static TraceEvent Step(string? target = null, string? error = null, int seq = 5) =>
        new TraceEvent { Seq = seq, Kind = "error", Target = target, ErrorDetail = error };

    [Fact]
    public void Target_wins() => Assert.Equal("By.Name: Save", TraceAnalysis.GuessLocator(Step("By.Name: Save", "using=name, value=Other")));

    [Theory]
    [InlineData("OpenQA.Selenium.NoSuchElementException: no element By.AccessibilityId: btnSubmitt\n at X", "By.AccessibilityId: btnSubmitt")]
    [InlineData("{\"using\":\"name\",\"value\":\"Customer nme\"}", "Customer nme")]
    [InlineData("Timed out waiting for accessibility id=btnSave after 10 s", "btnSave")]
    [InlineData("Element Name='Customer name' not found", "Customer name")]
    public void Finds_locator_in_error_text(string error, string expected) =>
        Assert.Equal(expected, TraceAnalysis.GuessLocator(Step(error: error)));

    [Fact]
    public void Falls_back_to_recent_logs()
    {
        var logs = new[] { new TraceEvent { Seq = 3, Kind = "log", Name = "waiting for name=Submit" } };
        Assert.Equal("Submit", TraceAnalysis.GuessLocator(Step(error: "Timeout"), logs));
    }

    [Fact]
    public void Returns_null_when_nothing_found() => Assert.Null(TraceAnalysis.GuessLocator(Step(error: "Timeout")));
}

public class EnvironmentTests
{
    static TraceMetadata Meta(params (string k, string v)[] env)
    {
        var m = new TraceMetadata();
        foreach (var (k, v) in env) m.Environment[k] = v;
        return m;
    }

    [Fact]
    public void Healthy_environment_has_no_warnings() =>
        Assert.Empty(TraceAnalysis.EnvironmentWarnings(Meta(("desktop", "Default"), ("desktopAtEnd", "Default"), ("interactive", "yes"), ("session", "console"), ("scaling", "100%"))));

    [Fact]
    public void Lock_screen_non_interactive_rdp_and_scaling_are_reported()
    {
        var w = TraceAnalysis.EnvironmentWarnings(Meta(("desktop", "Default"), ("desktopAtEnd", "Winlogon"), ("interactive", "no"), ("session", "remote desktop"), ("scaling", "150%")));
        Assert.Equal(4, w.Count);
        Assert.Contains(w, x => x.Contains("at the end") && x.Contains("Winlogon"));
        Assert.Contains(w, x => x.Contains("non-interactive"));
        Assert.Contains(w, x => x.Contains("Remote Desktop"));
        Assert.Contains(w, x => x.Contains("150%"));
    }

    [Fact]
    public void Recorder_captures_environment_and_reports_it()
    {
        var rec = new TraceRecorder();
        var env = rec.GetMetadata().Environment;
        Assert.True(env.ContainsKey(".NET"));
        Assert.True(env.ContainsKey("interactive"));
        if (System.OperatingSystem.IsWindows()) Assert.True(env.ContainsKey("desktop") || env.ContainsKey("screen"));

        rec.SetOutcome(true);
        var md = TraceReport.Markdown(rec.GetMetadata(), rec.Events, _ => null);
        Assert.Contains("## Environment", md);

        var off = new TraceRecorder(null, new TraceOptions { CaptureEnvironment = false });
        Assert.Empty(off.GetMetadata().Environment);
    }
}
