-- V0004: return the stored traceparent from the claim, so the worker can start its execution span with a span link to
-- the submission trace (ADR-0012). Changing a function's return columns requires DROP + CREATE; re-grant afterwards.
-- Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

DROP FUNCTION aiep.claim_next_run(text, uuid);

CREATE FUNCTION aiep.claim_next_run(p_worker_id text, p_attempt_id uuid)
RETURNS TABLE (
    run_id uuid, organization_id uuid, workspace_id uuid, attempt_id uuid, attempt_number integer, max_attempts integer,
    input jsonb, handler_type text, handler_version text, model_provider text, model_name text, model_config jsonb,
    prompt_template text, output_schema jsonb, timeout_seconds integer, lease_seconds integer,
    lease_expires_at timestamptz, traceparent text, cancel_requested boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = aiep, pg_temp
AS $$
DECLARE
    v_run_id        uuid;
    v_lease_seconds integer;
    v_attempt_number integer;
BEGIN
    SELECT r.id, v.lease_seconds
      INTO v_run_id, v_lease_seconds
    FROM aiep.task_runs r
    JOIN aiep.task_definition_versions v ON v.id = r.task_definition_version_id
    WHERE r.status IN ('Queued', 'RetryWait')
      AND r.next_attempt_at <= now()
      AND r.cancel_requested_at IS NULL
    ORDER BY r.next_attempt_at
    FOR UPDATE OF r SKIP LOCKED
    LIMIT 1;

    IF v_run_id IS NULL THEN
        RETURN;
    END IF;

    UPDATE aiep.task_runs r
    SET status = 'Running',
        attempt_count = r.attempt_count + 1,
        lease_owner = p_worker_id,
        lease_expires_at = now() + make_interval(secs => v_lease_seconds),
        next_attempt_at = NULL,
        started_at = COALESCE(r.started_at, now()),
        updated_at = now()
    WHERE r.id = v_run_id
    RETURNING r.attempt_count INTO v_attempt_number;

    INSERT INTO aiep.task_run_attempts (id, organization_id, workspace_id, run_id, attempt_number, status, worker_id)
    SELECT p_attempt_id, r.organization_id, r.workspace_id, r.id, v_attempt_number, 'running', p_worker_id
    FROM aiep.task_runs r WHERE r.id = v_run_id;

    RETURN QUERY
    SELECT r.id, r.organization_id, r.workspace_id, p_attempt_id, v_attempt_number, r.max_attempts,
           r.input, v.handler_type, v.handler_version, v.model_provider, v.model_name, v.model_config,
           v.prompt_template, v.output_schema, v.timeout_seconds, v.lease_seconds,
           r.lease_expires_at, r.traceparent, (r.cancel_requested_at IS NOT NULL)
    FROM aiep.task_runs r
    JOIN aiep.task_definition_versions v ON v.id = r.task_definition_version_id
    WHERE r.id = v_run_id;
END;
$$;

REVOKE ALL ON FUNCTION aiep.claim_next_run(text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION aiep.claim_next_run(text, uuid) TO aiep_worker;
