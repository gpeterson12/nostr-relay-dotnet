CREATE INDEX IF NOT EXISTS idx_events_pubkey_kind ON events(pubkey, kind);
CREATE INDEX IF NOT EXISTS idx_events_kind_created_at ON events(kind, created_at);
CREATE INDEX IF NOT EXISTS idx_events_created_at ON events(created_at);
CREATE INDEX IF NOT EXISTS idx_events_pubkey_kind_dtag ON events(pubkey, kind, d_tag);
CREATE INDEX IF NOT EXISTS idx_events_expires_at ON events(expires_at);
CREATE INDEX IF NOT EXISTS idx_event_tags_name_value ON event_tags(tag_name, tag_value);
CREATE INDEX IF NOT EXISTS idx_event_tags_event_id ON event_tags(event_id);

-- Optional fast path (Section 5.2): the normalized event_tags table above is the actual
-- tag-filtering mechanism this codebase uses; this GIN index just keeps the door open for
-- ad-hoc JSONB containment queries (`tags @> '...'`) later without a migration.
CREATE INDEX IF NOT EXISTS idx_events_tags_gin ON events USING GIN (tags);
