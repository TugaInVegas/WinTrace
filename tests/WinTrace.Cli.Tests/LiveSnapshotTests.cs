using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WinTrace.Cli;
using Xunit;

/// <summary>A tiny fake WebDriver server: records requests and answers like WinAppDriver (legacy) or Appium (W3C).</summary>
sealed class FakeDriver : IDisposable
{
    readonly HttpListener _listener = new HttpListener();
    readonly bool _w3c;
    public readonly ConcurrentQueue<(string Method, string Path, string Body)> Requests = new ConcurrentQueue<(string, string, string)>();
    public string Url { get; }
    public string WindowTitle = "Sample App";
    int _sessions;

    public const string Tree = "<Window Name=\"Sample App\" AutomationId=\"MainWindow\"><Button Name=\"Save\" AutomationId=\"btnSave\"/><Button Name=\"Submit\" AutomationId=\"btnSubmit\"/><Edit Name=\"Customer name\" AutomationId=\"txtCustomerName\"/></Window>";

    public FakeDriver(bool w3c = false)
    {
        _w3c = w3c;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        Task.Run(Loop);
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            var body = new System.IO.StreamReader(ctx.Request.InputStream).ReadToEnd();
            var path = ctx.Request.Url!.AbsolutePath;
            Requests.Enqueue((ctx.Request.HttpMethod, path, body));
            var (status, json) = Handle(ctx.Request.HttpMethod, path, body);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
    }

    (int, string) Handle(string method, string path, string body)
    {
        var parts = path.Trim('/').Split('/');
        if (method == "POST" && path == "/session")
        {
            var id = "s" + Interlocked.Increment(ref _sessions);
            return (200, _w3c ? $"{{\"value\":{{\"sessionId\":\"{id}\",\"capabilities\":{{}}}}}}" : $"{{\"sessionId\":\"{id}\",\"status\":0,\"value\":{{}}}}");
        }
        if (method == "DELETE") return (200, "{\"value\":null}");
        if (parts.Length >= 3 && parts[2] == "element" && method == "POST")
        {
            var value = JsonNode.Parse(body)!["value"]!.GetValue<string>();
            var found = value == WindowTitle || (value.Contains("contains") && value.Contains(WindowTitle.Split(' ')[0]));
            if (!found) return (404, "{\"value\":{\"error\":\"no such element\",\"message\":\"An element could not be located\"}}");
            return (200, _w3c ? "{\"value\":{\"element-6066-11e4-a52e-4f735466cecf\":\"42\"}}" : "{\"status\":0,\"value\":{\"ELEMENT\":\"42\"}}");
        }
        if (path.EndsWith("/attribute/NativeWindowHandle")) return (200, "{\"value\":\"6699\"}");
        if (path.EndsWith("/source")) return (200, "{\"value\":" + JsonValue.Create(Tree)!.ToJsonString() + "}");
        if (path.EndsWith("/screenshot")) return (200, "{\"value\":\"iVBORw==\"}");
        return (404, "{\"value\":{\"error\":\"unknown command\",\"message\":\"" + path + "\"}}");
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

public class LiveSnapshotTests
{
    [Fact]
    public async Task Window_mode_attaches_by_handle_reads_tree_and_deletes_its_sessions()
    {
        using var d = new FakeDriver();
        var r = await LiveSnapshot.TakeAsync(new LiveSnapshot.Request { Url = d.Url, Window = "Sample App", WantScreenshot = true });

        Assert.Equal(FakeDriver.Tree, r.Source);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, r.Screenshot);
        var reqs = d.Requests.ToList();
        var creates = reqs.Where(q => q.Method == "POST" && q.Path == "/session").ToList();
        Assert.Equal(2, creates.Count);
        Assert.Contains("\"app\":\"Root\"", creates[0].Body);
        Assert.Contains("\"appTopLevelWindow\":\"0x1A2B\"", creates[1].Body);   // 6699 decimal
        Assert.Equal(2, reqs.Count(q => q.Method == "DELETE"));
        Assert.Equal("s2", r.SessionId);
    }

    [Fact]
    public async Task Window_title_falls_back_to_contains_and_reports_missing_windows()
    {
        using var d = new FakeDriver { WindowTitle = "Sample App - Customers" };
        var r = await LiveSnapshot.TakeAsync(new LiveSnapshot.Request { Url = d.Url, Window = "Sample" });
        Assert.NotEmpty(r.Source);

        using var d2 = new FakeDriver();
        var ex = await Assert.ThrowsAsync<LiveSnapshotException>(() =>
            LiveSnapshot.TakeAsync(new LiveSnapshot.Request { Url = d2.Url, Window = "Nope" }));
        Assert.Contains("No top-level window", ex.Message);
        Assert.Equal(1, d2.Requests.Count(q => q.Method == "DELETE"));   // the root session is cleaned up
    }

    [Fact]
    public async Task Existing_session_is_only_read_never_created_or_deleted()
    {
        using var d = new FakeDriver();
        await LiveSnapshot.TakeAsync(new LiveSnapshot.Request { Url = d.Url, Session = "abc" });
        var reqs = d.Requests.ToList();
        Assert.Single(reqs);
        Assert.Equal("/session/abc/source", reqs[0].Path);
    }

    [Fact]
    public async Task Appium_w3c_uses_prefixed_capabilities()
    {
        using var d = new FakeDriver(w3c: true);
        var r = await LiveSnapshot.TakeAsync(new LiveSnapshot.Request { Url = d.Url, Window = "Sample App", AutomationName = "NovaWindows" });
        var creates = d.Requests.Where(q => q.Method == "POST" && q.Path == "/session").ToList();
        Assert.Contains("\"appium:automationName\":\"NovaWindows\"", creates[0].Body);
        Assert.Contains("\"appium:appTopLevelWindow\":\"0x1A2B\"", creates[1].Body);
        Assert.DoesNotContain("desiredCapabilities", creates[0].Body);
        Assert.Equal(FakeDriver.Tree, r.Source);
    }

    [Fact]
    public void Search_puts_exact_and_contains_matches_before_similar_ones()
    {
        var results = LiveSnapshot.Search(FakeDriver.Tree, "submit");
        Assert.Equal("btnSubmit", results[0].Element.AutomationId);
        Assert.Equal(1.0, results[0].Score);
        Assert.Contains(LiveSnapshot.Search(FakeDriver.Tree, "customer nme"), s => s.Element.Name == "Customer name");
    }
}
