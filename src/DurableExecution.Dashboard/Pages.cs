using System.Net;
using DurableExecution.History;

namespace DurableExecution.Dashboard;

/// <summary>
/// The dashboard's plain server-rendered pages. An operator tool, not a
/// design exercise: tables, status words, and the engine's own refusal
/// text when a history is corrupt. Every interpolated value is
/// HTML-encoded.
/// </summary>
public static class Pages
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Layout(string title, string body) =>
        "<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\" />" +
        $"<title>{E(title)} — DurableExecution</title></head><body>" +
        "<h1>DurableExecution — operator dashboard</h1>" +
        body +
        "</body></html>\n";

    public static string InstanceList(IReadOnlyList<InstanceSummary> instances, string provider)
    {
        var rows = string.Join("\n", instances.Select(i =>
            $"<tr><td><a href=\"/instances/{Uri.EscapeDataString(i.InstanceId)}\">{E(i.InstanceId)}</a></td>" +
            $"<td>{E(i.Status)}</td><td>{E(i.DefinitionId)}</td><td>{i.EventCount}</td>" +
            $"<td>{E(i.Note)}</td></tr>"));
        if (instances.Count == 0)
            rows = "<tr><td colspan=\"5\">No instances found.</td></tr>";
        return Layout("Instances",
            $"<p>Provider: {E(provider)}. Status is read from each instance's sealed history on every page load.</p>" +
            "<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">" +
            "<tr><th>Instance</th><th>Status</th><th>Definition</th><th>Events</th><th>Note</th></tr>" +
            rows + "</table>");
    }

    public static string InstanceDetail(InstanceDetail detail, string? notice)
    {
        var summary = detail.Summary;
        var body = new System.Text.StringBuilder();
        body.Append($"<p><a href=\"/\">All instances</a></p>");
        body.Append($"<h2>Instance {E(summary.InstanceId)}</h2>");
        body.Append($"<p>Status: <strong>{E(summary.Status)}</strong> · Definition: {E(summary.DefinitionId)} · Events: {summary.EventCount}</p>");
        if (notice is not null)
            body.Append($"<p><strong>{E(notice)}</strong></p>");

        if (detail.Corruption is not null)
        {
            body.Append("<h3 style=\"color:#a00\">CORRUPT HISTORY</h3>");
            body.Append($"<p>The store refused to load this history; nothing below is reconstructed or guessed. Refusal: {E(detail.Corruption)}</p>");
        }
        else
        {
            body.Append("<p>History loaded through the store's validating load while rendering this page — every record's seal and structure verified.</p>");
            body.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">");
            body.Append("<tr><th>n</th><th>Type</th><th>Timestamp (UTC)</th><th>Details</th></tr>");
            foreach (var historyEvent in detail.Events!)
            {
                body.Append($"<tr><td>{historyEvent.N}</td><td>{E(historyEvent.Type)}</td>" +
                    $"<td>{E(historyEvent.GetString("ts"))}</td><td>{E(EventDetails(historyEvent))}</td></tr>");
            }
            body.Append("</table>");

            body.Append("<h3>Send a signal</h3>");
            body.Append("<p>The signal is appended to this history through the store — the same append any " +
                "external sender makes. It is delivered when the workflow next waits for it, or on resume.</p>");
            body.Append($"<form method=\"post\" action=\"/instances/{Uri.EscapeDataString(summary.InstanceId)}/signal\">");
            body.Append("<label>Signal name <input name=\"name\" required /></label> ");
            body.Append("<label>Payload (JSON or text) <input name=\"payload\" /></label> ");
            body.Append("<button type=\"submit\">Send signal</button></form>");
        }
        return Layout($"Instance {summary.InstanceId}", body.ToString());
    }

    public static string NotFound(string instanceId) =>
        Layout("Not found",
            $"<p><a href=\"/\">All instances</a></p><h2>No such instance: {E(instanceId)}</h2>");

    public static string Error(string message) =>
        Layout("Error", $"<p><a href=\"/\">All instances</a></p><h2>Request failed</h2><p>{E(message)}</p>");

    private static string EventDetails(HistoryEvent historyEvent)
    {
        var parts = new List<string>();
        void Add(string label, object? value)
        {
            if (value is not null)
                parts.Add($"{label}={value}");
        }
        Add("def", historyEvent.GetString("def"));
        Add("seq", historyEvent.CommandSequence);
        Add("activity", historyEvent.GetString("activity"));
        Add("attempt", historyEvent.GetInt64("attempt"));
        Add("name", historyEvent.GetString("name"));
        Add("changeId", historyEvent.GetString("changeId"));
        Add("version", historyEvent.GetInt64("version"));
        Add("dueAt", historyEvent.GetString("dueAt"));
        Add("error", historyEvent.GetString("error"));
        var result = historyEvent.GetNode("result");
        if (result is not null)
            parts.Add($"result={result.ToJsonString()}");
        return string.Join(" · ", parts);
    }
}
