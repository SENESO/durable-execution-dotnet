using System.Net;
using DurableExecution.Dashboard;
using DurableExecution.Execution;
using DurableExecution.History;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The operator dashboard over HTTP, hosted in-process by
/// TestServer (real HTTP semantics, no sockets — which also makes the
/// suite runnable where loopback TCP from test processes is restricted). The list reads each instance's status from
/// its sealed history; the detail renders the verified event list; a
/// history the store refuses renders as CORRUPT with the refusal text,
/// never as a partial or reconstructed view; a posted signal lands in
/// history through the store's own append path.
/// </summary>
public sealed class DashboardTests : IDisposable
{
    private readonly string _root;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public DashboardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "p0-dashboard", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        SeedCompleted("inst-a");
        SeedActive("inst-b");
        SeedCorrupt("inst-corrupt");

        // The same route table the running app maps (DashboardApp.MapRoutes),
        // hosted on TestServer: real HTTP semantics, no sockets.
        var config = new DashboardConfig("file", _root, null, null);
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton<DashboardData>();
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => DashboardApp.MapRoutes(endpoints));
            });
        _server = new TestServer(builder);
        _client = _server.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test", "dashboard");
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private void SeedCompleted(string instance)
    {
        string dir = Path.Combine(_root, instance);
        using var host = new WorkflowHost(new FileHistoryStore(dir), dir);
        var outcome = host.RunAsync("order/v1", instance, new { x = 1 }, async ctx =>
        {
            await ctx.CallActivityAsync("ChargeCard", new { amount = 5 }, actx => Task.FromResult("txn-abc"));
            await ctx.CallActivityAsync("ReserveStock", new { orderId = "o" }, actx => Task.FromResult("ok"));
            return "txn-abc";
        }).GetAwaiter().GetResult();
        Assert.Equal(WorkflowRunOutcome.Completed, outcome);
    }

    private void SeedActive(string instance)
    {
        string dir = Path.Combine(_root, instance);
        var store = new FileHistoryStore(dir);
        store.Load();
        store.Append(HistoryEvent.WorkflowStarted(instance, "order/v1",
            Fingerprints.InputFingerprint(new { x = 1 })));
        store.Append(HistoryEvent.ActivityScheduled(instance, 1, "ChargeCard",
            Fingerprints.DeriveIdempotencyKey("default", instance, 1, "ChargeCard"),
            Fingerprints.InputFingerprint(new { amount = 5 })));
    }

    private void SeedCorrupt(string instance)
    {
        SeedCompleted(instance);
        string path = Path.Combine(_root, instance, "history.log");
        // Rewrite a recorded result in place: still valid JSON, wrong seal.
        File.WriteAllText(path, File.ReadAllText(path).Replace("txn-abc", "txn-XYZ"));
    }

    [Fact]
    public async Task List_ShowsEachInstanceWithItsHistoryDerivedStatus()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("inst-a", html);
        Assert.Contains("Completed", html);
        Assert.Contains("inst-b", html);
        Assert.Contains("Active", html);
        Assert.Contains("inst-corrupt", html);
        Assert.Contains("Corrupt", html);
    }

    [Fact]
    public async Task Detail_RendersTheVerifiedEventList()
    {
        var response = await _client.GetAsync("/instances/inst-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("WorkflowStarted", html);
        Assert.Contains("ChargeCard", html);
        Assert.Contains("WorkflowCompleted", html);
        Assert.Contains("validating load", html);
    }

    [Fact]
    public async Task Detail_CorruptHistory_ShowsAsCorruptWithTheRefusal()
    {
        var response = await _client.GetAsync("/instances/inst-corrupt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("CORRUPT HISTORY", html);
        Assert.Contains("checksum mismatch", html);
        Assert.DoesNotContain("txn-XYZ", html); // nothing reconstructed or guessed
    }

    [Fact]
    public async Task Signal_Post_AppendsThroughTheStore_AndShowsInHistory()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "approve",
            ["payload"] = "123",
        });
        var post = await _client.PostAsync("/instances/inst-b/signal", form);
        Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        Assert.Contains("sent=1", post.Headers.Location!.ToString());

        // The record is in history itself, appended through the store.
        var store = new FileHistoryStore(Path.Combine(_root, "inst-b"));
        var events = store.Load().Events;
        var signal = Assert.Single(events, e => e.Type == HistoryEventTypes.SignalReceived);
        Assert.Equal("approve", signal.GetString("name"));
        Assert.Equal(123, signal.GetNode("result")!.GetValue<int>());

        var detail = await _client.GetAsync("/instances/inst-b");
        string html = await detail.Content.ReadAsStringAsync();
        Assert.Contains("SignalReceived", html);
        Assert.Contains("approve", html);
    }

    [Fact]
    public async Task Signal_ToCompletedInstance_IsRefused_AndHistoryStaysValid()
    {
        // A completed workflow can never consume a signal, and nothing
        // may be appended after WorkflowCompleted (the load validator
        // refuses it) — so the dashboard refuses loudly instead of
        // appending a record that would poison the history.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "approve",
            ["payload"] = "1",
        });
        var post = await _client.PostAsync("/instances/inst-a/signal", form);
        Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        Assert.Contains("error=", post.Headers.Location!.ToString());

        var store = new FileHistoryStore(Path.Combine(_root, "inst-a"));
        var events = store.Load().Events; // still loads: nothing was appended
        Assert.DoesNotContain(events, e => e.Type == HistoryEventTypes.SignalReceived);
        Assert.Contains(events, e => e.Type == HistoryEventTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task Detail_UnknownInstance_Is404()
    {
        var response = await _client.GetAsync("/instances/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
