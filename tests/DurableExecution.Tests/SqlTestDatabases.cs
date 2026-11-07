using Microsoft.Data.SqlClient;

namespace DurableExecution.Tests;

/// <summary>
/// Scratch-database provisioning for the SQL Server conformance runs (§6.5).
/// The suite never carries a connection string in the repository: the master
/// connection (to <c>master</c>, under a login allowed to create and drop
/// databases) arrives in the <c>DE_SQLSERVER_CONNECTION</c> environment
/// variable of the test run, and every scenario/test gets its own throwaway
/// database, dropped when the test class is disposed. If the variable is
/// absent the SQL Server test classes are excluded by trait (see
/// run-tests.sh); reaching this helper without it is a misconfiguration and
/// fails loudly.
/// </summary>
public static class SqlTestDatabases
{
    public const string MasterConnectionVariable = "DE_SQLSERVER_CONNECTION";

    public static string MasterConnection =>
        Environment.GetEnvironmentVariable(MasterConnectionVariable)
        ?? throw new InvalidOperationException(
            $"{MasterConnectionVariable} is not set — the SQL Server conformance classes " +
            "require a test SQL Server (they are trait-excluded when it is absent).");

    /// <summary>Creates a uniquely named scratch database and returns the connection string for it.</summary>
    public static (string Name, string Connection) CreateDatabase(string tag)
    {
        string name = "de_p1_" + tag.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = new SqlConnection(MasterConnection);
        conn.Open();
        using var cmd = new SqlCommand($"CREATE DATABASE [{name}]", conn);
        cmd.ExecuteNonQuery();
        var builder = new SqlConnectionStringBuilder(MasterConnection) { InitialCatalog = name };
        return (name, builder.ConnectionString);
    }

    public static void DropDatabase(string name)
    {
        using var conn = new SqlConnection(MasterConnection);
        conn.Open();
        using var cmd = new SqlCommand(
            $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs a statement batch against one scratch database.</summary>
    public static void Execute(string connection, string sql)
    {
        using var conn = new SqlConnection(connection);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads the history payloads of one instance, in event order.</summary>
    public static List<string> ReadPayloads(string connection, string instanceId)
    {
        var payloads = new List<string>();
        using var conn = new SqlConnection(connection);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT Payload FROM dbo.HistoryEvents WHERE InstanceId = @instance ORDER BY N", conn);
        cmd.Parameters.Add(new SqlParameter("@instance", System.Data.SqlDbType.NVarChar, 128) { Value = instanceId });
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            payloads.Add(reader.GetString(0));
        return payloads;
    }
}
