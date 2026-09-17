using FluentAssertions.Execution;
using FluentAssertions.Primitives;
using Rtfm.Core;

namespace Rtfm.Tests;

/// <summary>
/// One small custom assertion, because "rejected, and on this field" is asserted
/// about twenty times and spelling it out each time buries what each test is for.
/// </summary>
public static class ValidationAssertions
{
    public static ValidationOutcomeAssertions Should(this ValidationOutcome outcome) => new(outcome);

    public sealed class ValidationOutcomeAssertions : ReferenceTypeAssertions<ValidationOutcome, ValidationOutcomeAssertions>
    {
        public ValidationOutcomeAssertions(ValidationOutcome subject)
            : base(subject)
        {
        }

        protected override string Identifier => "validation outcome";

        public AndConstraint<ValidationOutcomeAssertions> BeRejectedOn(string field, string because = "", params object[] becauseArgs)
        {
            Execute.Assertion
                .BecauseOf(because, becauseArgs)
                .ForCondition(!Subject.IsValid)
                .FailWith("Expected the submission to be rejected on {0}, but it was accepted.", field)
                .Then
                .ForCondition(Subject.Failures.Any(failure => failure.Field == field))
                .FailWith(
                    "Expected a failure on {0}, but the failures were on {1}.",
                    field,
                    Subject.Failures.Select(failure => failure.Field));

            return new AndConstraint<ValidationOutcomeAssertions>(this);
        }
    }
}
