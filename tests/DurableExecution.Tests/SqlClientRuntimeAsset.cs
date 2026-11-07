using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DurableExecution.Tests;

/// <summary>
/// Makes Microsoft.Data.SqlClient loadable under the xunit console runner.
/// The package ships a small platform facade in its <c>lib</c> asset — its
/// connection constructor throws <see cref="PlatformNotSupportedException"/>
/// by design — and the real per-platform implementation under
/// <c>runtimes/{rid}/lib</c>, which a normal application host selects via
/// the app's deps.json runtime targets. The console runner probes the test
/// directory flat and loads the facade instead. This initializer therefore
/// loads the platform implementation (same assembly identity) into the test
/// assembly's load context before anything can resolve the name to the
/// facade; later resolutions unify to the assembly already loaded. (Under
/// vstest the testhost is a normal app, the deps selection already picked
/// this same file, and the pre-load is a no-op unification.)
/// </summary>
internal static class SqlClientRuntimeAsset
{
    [ModuleInitializer]
    internal static void Register()
    {
        string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "unix";
        string dir = Path.GetDirectoryName(typeof(SqlClientRuntimeAsset).Assembly.Location)!;
        string candidate = Path.Combine(dir, "runtimes", rid, "lib", "net8.0", "Microsoft.Data.SqlClient.dll");
        if (!File.Exists(candidate))
            return;
        var context = AssemblyLoadContext.GetLoadContext(typeof(SqlClientRuntimeAsset).Assembly);
        if (context is null)
            return;
        try
        {
            context.LoadFromAssemblyPath(candidate);
        }
        catch (FileLoadException)
        {
            // An assembly with this identity is already loaded in the
            // context (a host that resolved it correctly); nothing to fix.
        }
    }
}
