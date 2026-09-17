namespace Rtfm.Core;

/// <summary>
/// What the store actually did with an upsert. The caller broadcasts on
/// <see cref="Created"/> and <see cref="Updated"/> only - see ADR-003.
/// </summary>
public enum IngestResult
{
    /// <summary>The message asserted nothing new: a duplicate, or an illegal transition.</summary>
    Ignored = 0,

    /// <summary>The transaction did not exist and a row was inserted.</summary>
    Created = 1,

    /// <summary>The transaction existed in Pending and moved to a terminal state.</summary>
    Updated = 2,
}
