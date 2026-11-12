CREATE TABLE user_suspensions (
    auth_subject text PRIMARY KEY CHECK (length(btrim(auth_subject)) > 0),
    reason varchar(500) NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
    suspended_by text NOT NULL CHECK (length(btrim(suspended_by)) > 0),
    suspended_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamptz,
    CHECK (expires_at IS NULL OR expires_at > suspended_at)
);
CREATE INDEX user_suspensions_expiry_idx ON user_suspensions(expires_at) WHERE expires_at IS NOT NULL;

CREATE TABLE admin_audit_log (
    id uuid PRIMARY KEY,
    actor_subject text NOT NULL CHECK (length(btrim(actor_subject)) > 0),
    action text NOT NULL CHECK (action IN ('match.terminated', 'user.suspended', 'user.unsuspended')),
    target_subject text,
    match_id uuid REFERENCES matches(id),
    reason varchar(500) NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
    details jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(details) = 'object'),
    occurred_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (target_subject IS NOT NULL OR match_id IS NOT NULL)
);
CREATE INDEX admin_audit_occurred_idx ON admin_audit_log(occurred_at DESC, id);
CREATE INDEX admin_audit_target_idx ON admin_audit_log(target_subject, occurred_at DESC) WHERE target_subject IS NOT NULL;
CREATE INDEX admin_audit_match_idx ON admin_audit_log(match_id, occurred_at DESC) WHERE match_id IS NOT NULL;

CREATE FUNCTION reject_admin_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Administrative audit is append-only' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER admin_audit_append_only BEFORE UPDATE OR DELETE ON admin_audit_log
    FOR EACH ROW EXECUTE FUNCTION reject_admin_audit_mutation();
CREATE TRIGGER admin_audit_no_truncate BEFORE TRUNCATE ON admin_audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION reject_admin_audit_mutation();
