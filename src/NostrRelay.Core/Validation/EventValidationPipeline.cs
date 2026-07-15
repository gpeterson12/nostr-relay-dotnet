namespace NostrRelay.Core.Validation;

/// <summary>
/// Runs an ordered chain of <see cref="IEventValidator"/> steps and stops at the first
/// failure. Order matters (Section 2.3): structural checks before id, id before
/// signature, signature before policy/kind-specific handling, each step trusts the
/// guarantees established by the ones before it.
///
/// Construct with <see cref="Default"/> for the standard rule-1-through-3 chain
/// (structural, id, signature). Policy and kind-specific steps (rules 4 and 5) get
/// appended once those layers exist (Milestones 5 and 8), by passing additional
/// validators into the constructor.
/// </summary>
public sealed class EventValidationPipeline(IReadOnlyList<IEventValidator> validators)
{
    public static EventValidationPipeline Default(Crypto.ISignatureVerifier signatureVerifier) =>
        new([
            new StructuralValidator(),
            new IdValidator(),
            new SignatureValidator(signatureVerifier)
        ]);

    public ValidationResult Validate(NostrEvent evt)
    {
        foreach (IEventValidator validator in validators)
        {
            ValidationResult result = validator.Validate(evt);
            if (!result.IsValid)
                return result;
        }

        return ValidationResult.Success();
    }
}
