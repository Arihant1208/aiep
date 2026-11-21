-- V0005: retention cleanup (Phase 3). A cross-tenant purge of expired idempotency keys and old terminal runs runs as a
-- SECURITY DEFINER function, like the queue's claim/reconcile, because the worker has no tenant context. Granted to the
-- worker only. Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- The purge runs as its owner aiep_migrator, which FORCE RLS binds, so it needs a definer policy on idempotency_keys to
-- reach rows across tenants (task_runs and task_run_attempts already have definer_queue_* policies from V0003).
CREATE POLICY definer_purge_idempotency ON aiep.idempotency_keys
    FOR ALL TO aiep_migrator USING (true) WITH CHECK (true);

-- purge_expired: delete idempotency keys past their expiry, and terminal runs whose completed_at is older than the
-- retention window, with their attempts and any remaining idempotency rows. Bounded by p_max_runs so a sweep is cheap.
CREATE FUNCTION aiep.purge_expired(p_retention_seconds integer, p_max_runs integer)
RETURNS TABLE (idempotency_keys_deleted integer, runs_deleted integer)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = aiep, pg_temp
AS $$
DECLARE
    v_keys integer;
    v_runs integer;
    v_cutoff timestamptz := now() - make_interval(secs => p_retention_seconds);
BEGIN
    DELETE FROM aiep.idempotency_keys WHERE expires_at < now();
    GET DIAGNOSTICS v_keys = ROW_COUNT;

    CREATE TEMP TABLE purge_runs ON COMMIT DROP AS
        SELECT id FROM aiep.task_runs
        WHERE status IN ('Succeeded', 'Failed', 'Cancelled', 'DeadLettered')
          AND completed_at IS NOT NULL
          AND completed_at < v_cutoff
        ORDER BY completed_at
        LIMIT p_max_runs;

    DELETE FROM aiep.idempotency_keys k USING purge_runs p WHERE k.run_id = p.id;
    DELETE FROM aiep.task_run_attempts a USING purge_runs p WHERE a.run_id = p.id;
    DELETE FROM aiep.task_runs r USING purge_runs p WHERE r.id = p.id;
    GET DIAGNOSTICS v_runs = ROW_COUNT;

    RETURN QUERY SELECT v_keys, v_runs;
END;
$$;

REVOKE ALL ON FUNCTION aiep.purge_expired(integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION aiep.purge_expired(integer, integer) TO aiep_worker;
