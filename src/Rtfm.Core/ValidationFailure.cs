namespace Rtfm.Core;

/// <summary>One rejected field and the reason. Maps directly onto a problem-details entry.</summary>
/// <param name="Field">The offending field, named as the wire contract names it.</param>
/// <param name="Message">Why it was rejected. Safe to return to the caller.</param>
public sealed record ValidationFailure(string Field, string Message);
