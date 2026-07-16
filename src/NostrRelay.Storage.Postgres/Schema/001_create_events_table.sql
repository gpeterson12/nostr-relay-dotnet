CREATE TABLE IF NOT EXISTS events (
    id         CHAR(64) PRIMARY KEY,
    pubkey     CHAR(64) NOT NULL,
    created_at BIGINT NOT NULL,
    kind       INTEGER NOT NULL,
    tags       JSONB NOT NULL,
    content    TEXT NOT NULL,
    sig        CHAR(128) NOT NULL,
    expires_at BIGINT NULL,
    d_tag      TEXT NULL
);
