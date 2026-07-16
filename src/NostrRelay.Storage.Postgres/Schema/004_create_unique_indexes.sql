-- Belt-and-suspenders, not the primary enforcement mechanism: PostgresEventStore already
-- serializes replaceable/addressable writes per key via pg_advisory_xact_lock before ever
-- reaching these indexes. These exist so that if application logic ever had a bug, the
-- database itself would refuse to end up with two rows for the same key, rather than
-- silently allowing it.
CREATE UNIQUE INDEX IF NOT EXISTS uq_events_replaceable ON events(pubkey, kind)
    WHERE kind = 0 OR kind = 3 OR (kind >= 10000 AND kind < 20000);

CREATE UNIQUE INDEX IF NOT EXISTS uq_events_addressable ON events(pubkey, kind, d_tag)
    WHERE kind >= 30000 AND kind < 40000;
