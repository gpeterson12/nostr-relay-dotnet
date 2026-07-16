CREATE TABLE IF NOT EXISTS event_tags (
    event_id  CHAR(64) NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    tag_name  TEXT NOT NULL,
    tag_value TEXT NOT NULL
);
