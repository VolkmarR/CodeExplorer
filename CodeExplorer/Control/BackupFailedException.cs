namespace CodeExplorer.Control;

/// <summary>
///     The control database's backup failed after the write it follows had committed. A type of its own
///     because a caller composing more than the write has to tell the two apart: the write is done, and
///     whatever else it removes must still be removed, where any other failure means the write did not
///     happen. A project delete is that caller — its index and local copies are removed after the row,
///     and a backup failure that stopped it left them for a project created later under the slug to
///     open (GHSA-253f-grfp-cqq7).
/// </summary>
public sealed class BackupFailedException(Exception innerException)
    : InvalidOperationException(
        "The change was saved, but backing the control database up to the durable store failed. "
        + "It is backed up with the next change that succeeds; until then a restart that wipes the disk loses it.",
        innerException);
