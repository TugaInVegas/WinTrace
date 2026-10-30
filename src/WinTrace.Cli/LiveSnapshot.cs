using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WinTrace.Cli
{
    public sealed class LiveSnapshotException : Exception
    {
        public LiveSnapshotException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads the live UI tree from WinAppDriver or an Appium server (NovaWindowsDriver) over plain WebDriver HTTP,
    /// so agents and people can find locators for new tests without Inspect.exe. Sessions it creates are deleted.
    /// </summary>
    public static class LiveSnapshot
    {
        public sealed class Request
        {
            public string Url { get; set; } = "http://127.0.0.1:4723";
            /// <summary>Read an existing session (for example a test paused at a breakpoint).</summary>
            public string? Session { get; set; }
            /// <summary>Attach to the top-level window with this title (exact, else containing it).</summary>
            public string? Window { get; set; }
            /// <summary>The whole desktop (slow on WinAppDriver for big trees).</summary>
            public bool Root { get; set; }
            /// <summary>Appium <c>automationName</c> (e.g. NovaWindows). When set, W3C capabilities are sent.</summary>
            public string? AutomationName { get; set; }
            public bool WantScreenshot { get; set; }
        }

        public sealed class Result
        {
            public string SessionId { get; set; } = "";
            public string Source { get; set; } = "";
            public byte[]? Screenshot { get; set; }
        }

        public static async Task<Result> TakeAsync(Request request, HttpClient? http = null)
        {
            var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var baseUrl = request.Url.TrimEnd('/');
            var created = new List<string>();
            try
            {
                string session;
                if (request.Session != null) session = request.Session;
                else
                {
                    var root = await CreateSession(client, baseUrl, request, "Root", null);
                    created.Add(root);
                    session = root;
                    if (request.Window != null)
                    {
                        var handle = await FindWindowHandle(client, baseUrl, root, request.Window);
                        session = await CreateSession(client, baseUrl, request, null, handle);
                        created.Add(session);
                        await Send(client, HttpMethod.Delete, $"{baseUrl}/session/{root}", null);
                        created.Remove(root);
                    }
                }

                var source = (await Send(client, HttpMethod.Get, $"{baseUrl}/session/{session}/source", null))?["value"]?.GetValue<string>()
                    ?? throw new LiveSnapshotException("The driver returned no page source.");
                byte[]? screenshot = null;
                if (request.WantScreenshot)
                {
                    var b64 = (await Send(client, HttpMethod.Get, $"{baseUrl}/session/{session}/screenshot", null))?["value"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(b64)) screenshot = Convert.FromBase64String(b64);
                }
                return new Result { SessionId = session, Source = source, Screenshot = screenshot };
            }
            finally
            {
                foreach (var id in created)
                    try { await Send(client, HttpMethod.Delete, $"{baseUrl}/session/{id}", null); }
                    catch (Exception ex) when (ex is HttpRequestException || ex is LiveSnapshotException || ex is TaskCanceledException) { }
                if (http == null) client.Dispose();
            }
        }

        /// <summary>Elements whose Name, AutomationId or ClassName contain <paramref name="text"/>, then ones that resemble it.</summary>
        public static IReadOnlyList<SimilarElement> Search(string source, string text, int max = 15)
        {
            var all = TraceAnalysis.ParseTree(source);
            bool Has(string? v) => v != null && v.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            bool Is(string? v) => v != null && string.Equals(v, text, StringComparison.OrdinalIgnoreCase);
            var direct = all.Where(e => Has(e.Name) || Has(e.AutomationId) || Has(e.ClassName))
                .Select(e => new SimilarElement { Element = e, Score = Is(e.Name) || Is(e.AutomationId) ? 1.0 : 0.95 });
            var similar = TraceAnalysis.FindSimilar(source, text, max);
            return direct.Concat(similar)
                .GroupBy(s => s.Element)
                .Select(g => g.OrderByDescending(s => s.Score).First())
                .OrderByDescending(s => s.Score)
                .Take(max)
                .ToList();
        }

        static async Task<string> CreateSession(HttpClient client, string baseUrl, Request request, string? app, string? topLevelWindow)
        {
            JsonObject body;
            if (request.AutomationName != null)
            {
                // Appium 2 (e.g. NovaWindowsDriver): W3C capabilities with vendor prefixes.
                var always = new JsonObject { ["platformName"] = "Windows", ["appium:automationName"] = request.AutomationName };
                if (app != null) always["appium:app"] = app;
                if (topLevelWindow != null) always["appium:appTopLevelWindow"] = topLevelWindow;
                body = new JsonObject { ["capabilities"] = new JsonObject { ["alwaysMatch"] = always } };
            }
            else
            {
                // WinAppDriver: the legacy and W3C shapes, as Selenium 3 clients send them.
                JsonObject Caps()
                {
                    var c = new JsonObject { ["platformName"] = "Windows" };
                    if (app != null) c["app"] = app;
                    if (topLevelWindow != null) c["appTopLevelWindow"] = topLevelWindow;
                    return c;
                }
                body = new JsonObject
                {
                    ["desiredCapabilities"] = Caps(),
                    ["capabilities"] = new JsonObject { ["firstMatch"] = new JsonArray(Caps()) }
                };
            }
            var res = await Send(client, HttpMethod.Post, $"{baseUrl}/session", body);
            var id = res?["sessionId"]?.GetValue<string>() ?? res?["value"]?["sessionId"]?.GetValue<string>();
            return id ?? throw new LiveSnapshotException("The driver did not return a session id. With Appium 2, pass --automation-name (e.g. NovaWindows).");
        }

        static async Task<string> FindWindowHandle(HttpClient client, string baseUrl, string rootSession, string title)
        {
            string? element = null;
            foreach (var (by, value) in new[] { ("name", title), ("xpath", $"/*/*[contains(@Name,{XPathLiteral(title)})]") })
            {
                try
                {
                    var res = await Send(client, HttpMethod.Post, $"{baseUrl}/session/{rootSession}/element", new JsonObject { ["using"] = by, ["value"] = value });
                    element = ElementId(res?["value"]);
                    if (element != null) break;
                }
                catch (LiveSnapshotException) { }
            }
            if (element == null)
                throw new LiveSnapshotException($"No top-level window named or containing \"{title}\". Use the window title shown in its title bar, or --root.");

            var handle = (await Send(client, HttpMethod.Get, $"{baseUrl}/session/{rootSession}/element/{element}/attribute/NativeWindowHandle", null))?["value"];
            var text = handle?.GetValueKind() == JsonValueKind.Number ? handle.GetValue<long>().ToString(CultureInfo.InvariantCulture) : handle?.GetValue<string>();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd) || hwnd == 0)
                throw new LiveSnapshotException($"The window \"{title}\" has no native window handle.");
            return "0x" + hwnd.ToString("X", CultureInfo.InvariantCulture);
        }

        static string? ElementId(JsonNode? value) =>
            value?["ELEMENT"]?.GetValue<string>() ?? value?["element-6066-11e4-a52e-4f735466cecf"]?.GetValue<string>();

        static string XPathLiteral(string s) =>
            !s.Contains('\'') ? $"'{s}'" : !s.Contains('"') ? $"\"{s}\"" : "concat('" + s.Replace("'", "',\"'\",'") + "')";

        static async Task<JsonNode?> Send(HttpClient client, HttpMethod method, string url, JsonNode? body)
        {
            using var msg = new HttpRequestMessage(method, url);
            if (body != null) msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(msg);
            var text = await res.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
            catch (JsonException) { }
            // W3C errors: { value: { error, message } }; legacy: { status: non-zero, value: { message } }.
            var error = json?["value"] is JsonObject v ? v["error"]?.GetValue<string>() : null;
            var status = json?["status"]?.GetValueKind() == JsonValueKind.Number ? json["status"]!.GetValue<int>() : 0;
            if (!res.IsSuccessStatusCode || error != null || status != 0)
            {
                var message = json?["value"]?["message"]?.GetValue<string>() ?? text;
                throw new LiveSnapshotException($"{method} {new Uri(url).AbsolutePath} failed ({(int)res.StatusCode}{(error != null ? ", " + error : "")}): {message}");
            }
            return json;
        }
    }
}
