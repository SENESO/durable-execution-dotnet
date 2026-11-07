using Npgsql;

namespace DurableExecution.Tests;

/// <summary>
/// Scratch-database provisioning for the PostgreSQL conformance runs (§6.5).
/// As with SQL Server, no connection string lives in the repository: the
/// master connection arrives in <c>DE_POSTGRES_CONNECTION</c>, under a login
/// allowed to create and drop databases, and each test gets a throwaway
/// database dropped on dispose. The classes are trait-excluded when the
/// variable is absent (see run-tests.sh).
/// </summary>
public static class PostgresTestDatabases
{
    public const string MasterConnectionVariable = "DE_POSTGRES_CONNECTION";

    public static string MasterConnection =>
        Environment.GetEnvironmentVariable(MasterConnectionVariable)
        ?? throw new InvalidOperationException(
            $"{MasterConnectionVariable} is not set — the PostgreSQL conformance classes " +
            "require a test PostgreSQL server (they are trait-excluded when it is absent).");

    public static (string Name, string Connection) CreateDatabase(string tag)
    {
        string name = "de_p1_pg_" + tag.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
        using var conn = new NpgsqlConnection(MasterConnection);
        conn.Open();
        using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        cmd.ExecuteNonQuery();
        var builder = new NpgsqlConnectionStringBuilder(MasterConnection) { Database = name };
        return (name, builder.ConnectionString);
    }

    public static void DropDatabase(string name)
    {
        using var conn = new NpgsqlConnection(MasterConnection);
        conn.Open();
        using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()", conn))
        {
            terminate.Parameters.Add(new NpgsqlParameter("@name", NpgsqlTypes.NpgsqlDbType.Text) { Value = name });
            terminate.ExecuteNonQuery();
        }
        using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\"", conn);
        drop.ExecuteNonQuery();
    }

    public static void Execute(string connection, string sql)
    {
        using var conn = new NpgsqlConnection(connection);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public static List<string> ReadPayloads(string connection, string instanceId)
    {
        var payloads = new List<string>();
        using var conn = new NpgsqlConnection(connection);
        conn.Open();
        using var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.history_events') IS NOT NULL", conn);
        if (!(bool)exists.ExecuteScalar()!)
            return payloads;
        using var cmd = new NpgsqlCommand(
            "SELECT payload FROM public.history_events WHERE instance_id = @instance ORDER BY n", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@instance", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = instanceId });
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            payloads.Add(reader.GetString(0));
        return payloads;
    }

    /// <summary>Creates the provider's table in a fresh scratch database (used before copying rows into a clone).</summary>
    public static void EnsureTable(string connection)
    {
        Execute(connection,
            "CREATE TABLE IF NOT EXISTS public.history_events (" +
            "  instance_id VARCHAR(128) NOT NULL, n BIGINT NOT NULL, payload TEXT NOT NULL," +
            "  PRIMARY KEY (instance_id, n));");
    }

    public static void InsertPayload(string connection, string instanceId, long n, string payload)
    {
        using var conn = new NpgsqlConnection(connection);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO public.history_events (instance_id, n, payload) VALUES (@instance, @n, @payload)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@instance", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = instanceId });
        cmd.Parameters.Add(new NpgsqlParameter("@n", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = n });
        cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Text) { Value = payload });
        cmd.ExecuteNonQuery();
    }
}
