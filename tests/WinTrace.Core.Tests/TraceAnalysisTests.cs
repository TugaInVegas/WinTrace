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
