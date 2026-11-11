CREATE TABLE matches (
    id uuid PRIMARY KEY,
    status text NOT NULL CHECK (status IN ('active', 'completed')),
    round_number smallint NOT NULL CHECK (round_number BETWEEN 1 AND 10),
    sequence_number bigint NOT NULL CHECK (sequence_number >= 0),
    first_player_id uuid NOT NULL,
    second_player_id uuid NOT NULL,
    first_health smallint NOT NULL CHECK (first_health BETWEEN 0 AND 50),
    second_health smallint NOT NULL CHECK (second_health BETWEEN 0 AND 50),
    first_energy integer NOT NULL CHECK (first_energy >= 0),
    second_energy integer NOT NULL CHECK (second_energy >= 0),
    first_reserved_energy integer NOT NULL DEFAULT 0 CHECK (first_reserved_energy >= 0),
    second_reserved_energy integer NOT NULL DEFAULT 0 CHECK (second_reserved_energy >= 0),
    first_used_blocks smallint NOT NULL CHECK (first_used_blocks BETWEEN 0 AND 5),
    second_used_blocks smallint NOT NULL CHECK (second_used_blocks BETWEEN 0 AND 5),
    first_used_special boolean NOT NULL,
    second_used_special boolean NOT NULL,
    winner_player_id uuid,
    last_activity_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at timestamptz,
    CHECK (first_player_id <> second_player_id),
    CHECK (winner_player_id IS NULL OR winner_player_id IN (first_player_id, second_player_id)),
    CHECK ((status = 'active' AND completed_at IS NULL AND winner_player_id IS NULL)
        OR (status = 'completed' AND completed_at IS NOT NULL)),
    CHECK (completed_at IS NULL OR completed_at >= created_at)
);

CREATE TABLE match_participants (
    match_id uuid NOT NULL REFERENCES matches(id),
    player_id uuid NOT NULL,
    auth_subject text NOT NULL CHECK (length(btrim(auth_subject)) > 0),
    nickname varchar(10) CHECK (nickname IS NULL OR length(btrim(nickname)) BETWEEN 1 AND 10),
    slot smallint NOT NULL CHECK (slot IN (1, 2)),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, player_id),
    UNIQUE (match_id, auth_subject),
    UNIQUE (match_id, slot)
);

-- Deferred validation gives exactly two matching slots at COMMIT without circular FKs.
CREATE FUNCTION validate_match_participants() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    target_id uuid;
    first_id uuid;
    second_id uuid;
BEGIN
    IF TG_TABLE_NAME = 'matches' THEN
        target_id := NEW.id;
    ELSE
        target_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.match_id ELSE NEW.match_id END;
        IF TG_OP = 'UPDATE' AND OLD.match_id <> NEW.match_id THEN
            RAISE EXCEPTION 'Participant match identity is immutable' USING ERRCODE = '23514';
        END IF;
    END IF;
    SELECT first_player_id, second_player_id INTO first_id, second_id
        FROM matches WHERE id = target_id FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF (SELECT count(*) FROM match_participants WHERE match_id = target_id) <> 2
        OR NOT EXISTS (SELECT 1 FROM match_participants WHERE match_id = target_id AND slot = 1 AND player_id = first_id)
        OR NOT EXISTS (SELECT 1 FROM match_participants WHERE match_id = target_id AND slot = 2 AND player_id = second_id) THEN
        RAISE EXCEPTION 'A match requires its two declared participants in matching slots' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER matches_participants_integrity
    AFTER INSERT OR UPDATE ON matches DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION validate_match_participants();
CREATE CONSTRAINT TRIGGER participants_match_integrity
    AFTER INSERT OR UPDATE OR DELETE ON match_participants DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION validate_match_participants();

CREATE TABLE match_events (
    match_id uuid NOT NULL REFERENCES matches(id),
    sequence_number bigint NOT NULL CHECK (sequence_number >= 0),
    event_type text NOT NULL CHECK (length(btrim(event_type)) > 0),
    event_version smallint NOT NULL CHECK (event_version > 0),
    occurred_at timestamptz NOT NULL,
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, sequence_number)
);
CREATE FUNCTION reject_event_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Match events are append-only' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER match_events_append_only BEFORE UPDATE OR DELETE ON match_events
    FOR EACH ROW EXECUTE FUNCTION reject_event_mutation();
CREATE TRIGGER match_events_no_truncate BEFORE TRUNCATE ON match_events
    FOR EACH STATEMENT EXECUTE FUNCTION reject_event_mutation();

CREATE TABLE match_commands (
    match_id uuid NOT NULL,
    player_id uuid NOT NULL,
    request_id uuid NOT NULL,
    request_fingerprint bytea NOT NULL CHECK (octet_length(request_fingerprint) = 32),
    accepted_sequence bigint NOT NULL CHECK (accepted_sequence >= 0),
    acknowledgement jsonb NOT NULL CHECK (jsonb_typeof(acknowledgement) = 'object'),
    outcome jsonb CHECK (outcome IS NULL OR jsonb_typeof(outcome) = 'object'),
    outcome_event_type text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, player_id, request_id),
    FOREIGN KEY (match_id, player_id) REFERENCES match_participants(match_id, player_id),
    CHECK ((outcome IS NULL AND outcome_event_type IS NULL)
        OR (outcome IS NOT NULL AND outcome_event_type IS NOT NULL AND length(btrim(outcome_event_type)) > 0))
);

CREATE TABLE pending_round_plans (
    match_id uuid NOT NULL,
    round_number smallint NOT NULL CHECK (round_number BETWEEN 1 AND 10),
    player_id uuid NOT NULL,
    request_id uuid NOT NULL,
    actions jsonb NOT NULL CHECK (jsonb_typeof(actions) = 'array'
        AND jsonb_array_length(actions) BETWEEN 1 AND 19
        AND actions <@ '["Attack", "Block", "Reserve", "SpecialAttack"]'::jsonb),
    submitted_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, round_number, player_id),
    UNIQUE (match_id, player_id, request_id),
    FOREIGN KEY (match_id, player_id, request_id) REFERENCES match_commands(match_id, player_id, request_id)
);
CREATE FUNCTION validate_pending_round() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM matches m JOIN match_commands c ON c.match_id = m.id
        AND c.player_id = NEW.player_id AND c.request_id = NEW.request_id
        WHERE m.id = NEW.match_id AND m.status = 'active'
        AND m.round_number = NEW.round_number AND m.sequence_number = c.accepted_sequence FOR UPDATE OF m;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Pending plan requires the active round' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER pending_round_integrity BEFORE INSERT OR UPDATE ON pending_round_plans
    FOR EACH ROW EXECUTE FUNCTION validate_pending_round();

CREATE FUNCTION audit_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;
CREATE TRIGGER matches_audit BEFORE UPDATE ON matches FOR EACH ROW EXECUTE FUNCTION audit_updated_at();
CREATE TRIGGER participants_audit BEFORE UPDATE ON match_participants FOR EACH ROW EXECUTE FUNCTION audit_updated_at();
CREATE TRIGGER commands_audit BEFORE UPDATE ON match_commands FOR EACH ROW EXECUTE FUNCTION audit_updated_at();

CREATE INDEX match_participants_subject_match_idx ON match_participants(auth_subject, match_id);
CREATE INDEX matches_status_activity_idx ON matches(status, last_activity_at);
