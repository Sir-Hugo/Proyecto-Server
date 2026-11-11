ALTER TABLE matches ADD COLUMN round_deadline_at timestamptz DEFAULT (CURRENT_TIMESTAMP + interval '5 minutes');
ALTER TABLE matches ADD COLUMN terminal_reason text;
ALTER TABLE matches ADD CHECK (status <> 'active' OR (round_deadline_at IS NOT NULL AND terminal_reason IS NULL));

CREATE FUNCTION normalize_match_deadline() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = 'completed' THEN NEW.round_deadline_at := NULL; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER matches_normalize_deadline BEFORE UPDATE ON matches
    FOR EACH ROW EXECUTE FUNCTION normalize_match_deadline();

CREATE INDEX matches_active_deadline_idx ON matches(round_deadline_at) WHERE status = 'active';

CREATE TABLE rooms (
    id uuid PRIMARY KEY,
    visibility text NOT NULL CHECK (visibility IN ('public', 'private')),
    invite_code varchar(12) UNIQUE,
    status text NOT NULL CHECK (status IN ('waiting', 'active', 'completed', 'closed')),
    match_id uuid UNIQUE REFERENCES matches(id),
    created_at timestamptz NOT NULL,
    last_activity_at timestamptz NOT NULL,
    CHECK ((visibility = 'private') = (invite_code IS NOT NULL)),
    CHECK ((status = 'waiting' AND match_id IS NULL)
        OR (status IN ('active', 'completed') AND match_id IS NOT NULL)
        OR status = 'closed')
);

CREATE TABLE room_members (
    room_id uuid NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    auth_subject text NOT NULL CHECK (length(btrim(auth_subject)) > 0),
    player_id uuid NOT NULL,
    is_owner boolean NOT NULL,
    is_ready boolean NOT NULL DEFAULT false,
    joined_at timestamptz NOT NULL,
    PRIMARY KEY (room_id, auth_subject),
    UNIQUE (room_id, player_id)
);
CREATE UNIQUE INDEX room_one_owner_idx ON room_members(room_id) WHERE is_owner;
CREATE INDEX room_members_subject_idx ON room_members(auth_subject);
CREATE INDEX rooms_public_waiting_idx ON rooms(created_at) WHERE visibility='public' AND status='waiting';

CREATE TABLE match_outbox (
    id uuid PRIMARY KEY,
    match_id uuid NOT NULL REFERENCES matches(id),
    target_subject text NOT NULL CHECK (length(btrim(target_subject)) > 0),
    event_type text NOT NULL CHECK (length(btrim(event_type)) > 0),
    sequence_number bigint NOT NULL CHECK (sequence_number >= 0),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    created_at timestamptz NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at timestamptz NOT NULL,
    delivered_at timestamptz,
    failed_at timestamptz,
    last_error text
);
CREATE INDEX match_outbox_pending_idx ON match_outbox(next_attempt_at, created_at) WHERE delivered_at IS NULL AND failed_at IS NULL;

-- Updates are last: migration 002 installed deferred triggers on matches, and PostgreSQL
-- rejects later DDL on a table once a deferred trigger event is pending in this transaction.
UPDATE matches SET round_deadline_at = last_activity_at + interval '5 minutes' WHERE status = 'active';
UPDATE matches SET round_deadline_at = NULL WHERE status = 'completed';
