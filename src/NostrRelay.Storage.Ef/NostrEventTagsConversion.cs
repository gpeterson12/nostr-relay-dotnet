using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NostrRelay.Storage.Ef;

/// <summary>
/// The JSON round-trip for <see cref="NostrEventEntity.Tags"/>, expressed as an EF Core
/// value conversion rather than as hand-written <c>JsonSerializer</c> calls on both sides of
/// the entity mapper.
///
/// Making this part of the model, not part of the mapper, means there is exactly one place
/// that knows the <c>tags</c> column holds JSON. It also means the entity exposes the same
/// nested-list shape the domain type does, so the mapper is a straight field copy with no
/// serialization step to get wrong in one direction and not the other.
///
/// The stored form is a plain JSON array of arrays, byte-identical to what the previous
/// hand-serialized <c>TagsJson</c> string produced, so this is a model-level change only:
/// the column type and contents are unchanged and no migration is required. Note that this
/// is *not* the NIP-01 canonical serialization (that lives in
/// <c>NostrEventCanonicalSerializer</c> and is only used for id computation); nothing about
/// event ids depends on how the column happens to be encoded.
///
/// A <see cref="ValueComparer{T}"/> is supplied alongside the converter because the property
/// is a mutable reference type. Without one, EF would compare snapshots by reference and
/// could miss changes. In this codebase reads are all <c>AsNoTracking</c> and writes are
/// insert-only, so the comparer is never on a hot path; correctness of the model matters
/// more here than shaving the round-trip.
/// </summary>
public static class NostrEventTagsConversion
{
    public static readonly ValueConverter<IReadOnlyList<IReadOnlyList<string>>, string> Converter =
        new(
            tags => JsonSerializer.Serialize(tags, (JsonSerializerOptions?)null),
            json => (IReadOnlyList<IReadOnlyList<string>>)(
                JsonSerializer.Deserialize<List<List<string>>>(json, (JsonSerializerOptions?)null)
                ?? new List<List<string>>()));

    public static readonly ValueComparer<IReadOnlyList<IReadOnlyList<string>>> Comparer =
        new(
            (left, right) =>
                JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) ==
                JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(),
            value => (IReadOnlyList<IReadOnlyList<string>>)(
                JsonSerializer.Deserialize<List<List<string>>>(
                    JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    (JsonSerializerOptions?)null)
                ?? new List<List<string>>()));
}
