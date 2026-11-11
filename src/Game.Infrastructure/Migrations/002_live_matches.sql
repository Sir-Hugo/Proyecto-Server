ALTER TABLE matches ADD COLUMN started_at timestamptz;
ALTER TABLE matches ADD CHECK (started_at IS NULL OR started_at >= created_at);
ALTER TABLE matches ADD CHECK (started_at IS NULL OR completed_at IS NULL OR completed_at >= started_at);

-- A durable occupancy ledger; it is updated by deferred triggers in the same commit.
CREATE TABLE active_match_players (
    auth_subject text PRIMARY KEY,
    match_id uuid NOT NULL,
    player_id uuid NOT NULL,
    UNIQUE (match_id, player_id),
    FOREIGN KEY (match_id, player_id) REFERENCES match_participants(match_id, player_id)
);
CREATE INDEX active_match_players_match_idx ON active_match_players(match_id);

-- Backfill fails on pre-existing conflicting active assignments rather than discarding data.
INSERT INTO active_match_players(auth_subject, match_id, player_id)
    SELECT p.auth_subject, p.match_id, p.player_id FROM match_participants p
    JOIN matches m ON m.id = p.match_id WHERE m.status = 'active'
    ORDER BY p.auth_subject;

CREATE FUNCTION sync_active_match_players() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE target_id uuid;
BEGIN
    IF TG_TABLE_NAME = 'matches' THEN target_id := NEW.id;
    ELSE target_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.match_id ELSE NEW.match_id END;
    END IF;
    DELETE FROM active_match_players WHERE match_id = target_id;
    INSERT INTO active_match_players(auth_subject, match_id, player_id)
        SELECT p.auth_subject, p.match_id, p.player_id FROM match_participants p
        JOIN matches m ON m.id = p.match_id WHERE m.id = target_id AND m.status = 'active'
        ORDER BY p.auth_subject;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER matches_active_assignments AFTER INSERT OR UPDATE ON matches
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sync_active_match_players();
CREATE CONSTRAINT TRIGGER participants_active_assignments AFTER INSERT OR UPDATE OR DELETE ON match_participants
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sync_active_match_players();

CREATE FUNCTION protect_completed_match() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'completed' AND (to_jsonb(NEW) - 'updated_at') IS DISTINCT FROM (to_jsonb(OLD) - 'updated_at') THEN
        RAISE EXCEPTION 'Completed match is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER completed_match_immutable BEFORE UPDATE ON matches
    FOR EACH ROW EXECUTE FUNCTION protect_completed_match();
