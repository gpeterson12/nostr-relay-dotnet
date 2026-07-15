using NostrRelay.Core.Serialization;

namespace NostrRelay.Core.Validation;

/// <summary>
/// Second pipeline step (Section 2.3 rule 2): recomputes the id from the canonical
/// serialization and rejects on mismatch. Must run after <see cref="StructuralValidator"/>
/// (so hex shape is already known-good) and before <see cref="SignatureValidator"/>
/// (which trusts Id to be the correct message hash).
/// </summary>
public sealed class IdValidator : IEventValidator
{
    public ValidationResult Validate(NostrEvent evt)
    {
        var expectedId = NostrEventCanonicalSerializer.ComputeId(evt);

        return string.Equals(expectedId, evt.Id, StringComparison.Ordinal)
            ? ValidationResult.Success()
            : ValidationResult.Failure("invalid: id does not match sha256 of canonical serialization");
    }
}
