namespace NostrRelay.Core.Validation;

/// <summary>
/// A single step in the event validation pipeline (Section 2.3). Steps are synchronous
/// and side-effect free by design at this stage: no storage or network I/O belongs here.
/// Policy checks that need external state (rate limits, allowlists) will implement this
/// interface too once Milestone 8 wires in the policy layer, but should take their
/// dependencies via constructor injection rather than reaching out globally.
/// </summary>
public interface IEventValidator
{
    ValidationResult Validate(NostrEvent evt);
}
