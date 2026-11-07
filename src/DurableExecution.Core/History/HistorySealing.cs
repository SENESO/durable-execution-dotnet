using System.Text.Json.Nodes;
using DurableExecution.Execution;

namespace DurableExecution.History;

/// <summary>
/// The seal of a history record (§6.1): a SHA-256 checksum over the record's
/// canonical content — every field except the checksum itself, serialized in
/// field order. Sealing is a property of the record, not of the storage
/// medium: every provider frames the identical sealed JSON payload, so a
/// history means the same thing on disk and in a database table.
/// </summary>
internal static class HistorySealing
{
    internal static string Canonical(JsonObject fields)
    {
        var copy = new JsonObject();
        foreach (var kv in fields)
            if (kv.Key != "sum")
                copy[kv.Key] = kv.Value?.DeepClone();
        return copy.ToJsonString();
    }

    internal static string Checksum(JsonObject fields) =>
        Fingerprints.Sha256Hex(Canonical(fields))[..16];
}
