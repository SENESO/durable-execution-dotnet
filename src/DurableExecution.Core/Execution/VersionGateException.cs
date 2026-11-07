namespace DurableExecution.Execution;

/// <summary>
/// The loud, named refusal of the P4 version gates: an instance's history
/// records a version for a change that this host's code no longer supports
/// (below its minimum or above its maximum for that change id). Resuming
/// such an instance with this code would silently take a branch the recorded
/// history did not take, so the run is refused before any command executes —
/// the operator must deploy code whose supported range covers the recorded
/// version, or abandon the instance explicitly. This is a deployment
/// mismatch, not corruption and not a determinism violation: the history is
/// valid; the code is the wrong version for it.
/// </summary>
public sealed class VersionGateException : Exception
{
    public VersionGateException(string changeId, int recordedVersion, int minSupported, int maxSupported)
        : base($"version gate refused the run: change '{changeId}' is recorded at version {recordedVersion} " +
               $"in this instance's history, but this host supports [{minSupported}..{maxSupported}] for it — " +
               "deploy code that supports the recorded version or abandon the instance; nothing was executed")
    {
        ChangeId = changeId;
        RecordedVersion = recordedVersion;
        MinSupported = minSupported;
        MaxSupported = maxSupported;
    }

    /// <summary>The change id whose recorded version is out of range.</summary>
    public string ChangeId { get; }

    /// <summary>The version the instance's history recorded for the change.</summary>
    public int RecordedVersion { get; }

    /// <summary>The lowest version this host's call site accepts.</summary>
    public int MinSupported { get; }

    /// <summary>The highest version this host's call site accepts (and records on a first live pass).</summary>
    public int MaxSupported { get; }
}
