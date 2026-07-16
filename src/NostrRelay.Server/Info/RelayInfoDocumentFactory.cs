using NostrRelay.Core.Protocol;

namespace NostrRelay.Server.Info;

/// <summary>
/// Builds the NIP-11 document once at startup from configuration (Section 5.6's "Relay"
/// section) plus the limits this relay genuinely enforces right now.
///
/// The <c>supported_nips</c> list is deliberately conservative: it lists only NIPs with a
/// complete, working implementation as of this milestone (1 and 11), not NIPs whose
/// storage plumbing exists but whose protocol-level behavior isn't wired up yet. For
/// example, NIP-40 expiration has a real <c>expires_at</c> column and
/// <c>DeleteExpiredEventsAsync</c>, but no periodic sweep job calls it yet (Milestone 9)
/// and no write-time validation of the "expiration" tag exists, so claiming NIP-40 support
/// here would be true in general shape but false in the specific, checkable sense NIP-11
/// implies. Update this list as each NIP's server-side behavior is actually completed, not
/// when its groundwork merely exists.
/// </summary>
public static class RelayInfoDocumentFactory
{
    public static RelayInfoDocument Create(IConfiguration configuration)
    {
        IConfigurationSection relaySection = configuration.GetSection("Relay");

        return new RelayInfoDocument
        {
            Name = relaySection["Name"],
            Description = relaySection["Description"],
            Pubkey = NullIfEmpty(relaySection["ContactPubkey"]),
            Contact = NullIfEmpty(relaySection["Contact"]),
            Software = relaySection["Software"],
            Version = relaySection["Version"],
            SupportedNips = [1, 11],
            Limitation = new RelayLimitationDocument
            {
                MaxMessageLength = RelayLimits.MaxMessageBytes,
                MaxSubscriptions = RelayLimits.MaxSubscriptionsPerConnection,
                MaxSubidLength = ClientMessageParser.MaxSubscriptionIdLength,
                DefaultLimit = RelayLimits.DefaultQueryLimit,

                // Explicitly false rather than omitted: these are accurate, deliberate
                // statements about the current relay ("no auth/payment/write-restriction
                // policy exists"), not placeholders. Compare to the fields left null above,
                // which mean "no claim made" rather than "claim: false".
                AuthRequired = false,
                PaymentRequired = false,
                RestrictedWrites = false,
            },
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
