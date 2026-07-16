CREATE TABLE IF NOT EXISTS events (
    id         TEXT PRIMARY KEY,
    pubkey     TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    kind       INTEGER NOT NULL,
    tags       TEXT NOT NULL,
    content    TEXT NOT NULL,
    sig        TEXT NOT NULL,
    expires_at INTEGER NULL,
    d_tag      TEXT NULL
);
