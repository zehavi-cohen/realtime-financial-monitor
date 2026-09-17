namespace Rtfm.IntegrationTests;

/// <summary>
/// The gate that keeps this suite out of <c>dotnet test</c> (S6).
/// </summary>
/// <remarks>
/// Opt-in by environment variable rather than a trait filter, because a trait only
/// helps the person who remembers to pass <c>--filter</c>. With this, a developer
/// with no Docker runs <c>dotnet test</c> and sees these reported as skipped, with
/// the reason attached - which is information, where a silently absent suite is not.
/// </remarks>
public static class Infrastructure
{
    public const string EnableVariable = "RTFM_INTEGRATION";

    public const string SkipReason =
        "Requires Docker. Set RTFM_INTEGRATION=1 to run the integration suite.";

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnableVariable) is "1" or "true" or "True";

    public static string? SkipUnlessEnabled => IsEnabled ? null : SkipReason;
}

/// <summary>A <see cref="FactAttribute"/> that skips itself unless the suite is enabled.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresInfrastructureFactAttribute : FactAttribute
{
    public RequiresInfrastructureFactAttribute() => Skip = Infrastructure.SkipUnlessEnabled;
}

/// <summary>A <see cref="TheoryAttribute"/> that skips itself unless the suite is enabled.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresInfrastructureTheoryAttribute : TheoryAttribute
{
    public RequiresInfrastructureTheoryAttribute() => Skip = Infrastructure.SkipUnlessEnabled;
}
