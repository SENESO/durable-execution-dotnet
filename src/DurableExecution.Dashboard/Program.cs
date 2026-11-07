using DurableExecution.Dashboard;

var app = DashboardApp.Build(args);
app.Run();

/// <summary>Composition marker for the dashboard assembly.</summary>
public partial class Program { }

/// <summary>The dashboard's composition: configuration, data path, routes.</summary>
public static class DashboardApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var config = DashboardConfig.FromEnvironment();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<DashboardData>();
        var app = builder.Build();
        MapRoutes(app);
        return app;
    }

    /// <summary>
    /// The route table, mappable on any endpoint route builder — the
    /// running app maps it on its WebApplication; the HTTP tests map the
    /// same table on a TestServer host.
    /// </summary>
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Text("ok"));

        routes.MapGet("/", (DashboardData data, DashboardConfig cfg) =>
            Results.Content(Pages.InstanceList(data.ListInstances(), cfg.Provider), "text/html"));

        routes.MapGet("/instances/{id}", (string id, DashboardData data, HttpRequest request) =>
        {
            var detail = data.GetInstance(id);
            if (detail is null)
                return Results.Content(Pages.NotFound(id), "text/html", statusCode: 404);
            string? notice = null;
            if (request.Query.ContainsKey("sent"))
                notice = "Signal recorded in history.";
            else if (request.Query.TryGetValue("error", out var error))
                notice = $"Signal refused: {error}";
            return Results.Content(Pages.InstanceDetail(detail, notice), "text/html");
        });

        routes.MapPost("/instances/{id}/signal", async (string id, DashboardData data, HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            string name = form["name"].ToString();
            string payload = form["payload"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                return Results.Redirect($"/instances/{Uri.EscapeDataString(id)}?error=name%20required");
            try
            {
                data.SendSignal(id, name, payload);
                return Results.Redirect($"/instances/{Uri.EscapeDataString(id)}?sent=1");
            }
            catch (Exception e)
            {
                return Results.Redirect(
                    $"/instances/{Uri.EscapeDataString(id)}?error={Uri.EscapeDataString(e.Message)}");
            }
        });
    }
}
