-- V0003: queue operations (Phase 2). The cross-tenant parts of the queue -- claiming the next run across all tenants,
-- and reconciling runs whose worker lease expired -- run as SECURITY DEFINER functions, because a worker has no tenant
-- context when it looks for work. Everything else (enqueue, heartbeat, settle) runs under the ordinary tenant unit of
-- work with the organization taken from the claimed run row (ADR-0005, ADR-0006, task-lifecycle.md).
--
-- Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- ---------------------------------------------------------------------------------------------------------------
-- Definer policies. FORCE RLS binds the table owner (aiep_migrator) too, and these functions run as that owner, so
-- give it full access to the queue tables (mirrors V0001's definer_identity_resolution / definer_api_key_lookup).
-- Services never connect as aiep_migrator; they reach these rows only through the fixed function bodies below.
-- ---------------------------------------------------------------------------------------------------------------
CREATE POLICY definer_queue_runs ON aiep.task_runs
    FOR ALL TO aiep_migrator USING (true) WITH CHECK (true);
CREATE POLICY definer_queue_attempts ON aiep.task_run_attempts
    FOR ALL TO aiep_migrator USING (true) WITH CHECK (true);
CREATE POLICY definer_queue_versions ON aiep.task_definition_versions
    FOR SELECT TO aiep_migrator USING (true);

-- ---------------------------------------------------------------------------------------------------------------
-- claim_next_run: pick one eligible run across all tenants, move it to Running with a lease taken from its definition
-- version, create its attempt row, and return everything the worker needs. FOR UPDATE OF r SKIP LOCKED makes
-- concurrent workers take different runs and never block each other. A due RetryWait run is promoted through Queued to
-- Running in one atomic claim (both legal transitions). Returns no row when nothing is eligible.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION aiep.claim_next_run(p_worker_id text, p_attempt_id uuid)
RETURNS TABLE (
    run_id uuid, organization_id uuid, workspace_id uuid, attempt_id uuid, attempt_number integer, max_attempts integer,
    input jsonb, handler_type text, handler_version text, model_provider text, model_name text, model_config jsonb,
    prompt_template text, output_schema jsonb, timeout_seconds integer, lease_seconds integer,
    lease_expires_at timestamptz, cancel_requested boolean)
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
           r.lease_expires_at, (r.cancel_requested_at IS NOT NULL)
    FROM aiep.task_runs r
    JOIN aiep.task_definition_versions v ON v.id = r.task_definition_version_id
    WHERE r.id = v_run_id;
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- reconcile_expired_leases: recover runs abandoned by a crashed or stalled worker. For each Running run whose lease has
-- passed, close the open attempt as lease-expired and either return it to the queue (attempts remain) or dead-letter it
-- (attempts exhausted). The lease timeout already provided the delay, so a retry becomes eligible immediately. Returns
-- how many runs it recovered. Runs across all tenants, so SECURITY DEFINER.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION aiep.reconcile_expired_leases(p_max_runs integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = aiep, pg_temp
AS $$
DECLARE
    v_count integer := 0;
    v_run   record;
BEGIN
    FOR v_run IN
        SELECT r.id, r.attempt_count, r.max_attempts
        FROM aiep.task_runs r
        WHERE r.status = 'Running' AND r.lease_expires_at < now()
        ORDER BY r.lease_expires_at
        FOR UPDATE SKIP LOCKED
        LIMIT p_max_runs
    LOOP
        UPDATE aiep.task_run_attempts
        SET status = 'lease_expired', ended_at = now(), error_category = 'LeaseExpired',
            error_message = 'worker lease expired'
        WHERE run_id = v_run.id AND status = 'running';

        IF v_run.attempt_count < v_run.max_attempts THEN
            UPDATE aiep.task_runs
            SET status = 'RetryWait', lease_owner = NULL, lease_expires_at = NULL, next_attempt_at = now(),
                error_category = 'LeaseExpired', error_message = 'worker lease expired', updated_at = now()
            WHERE id = v_run.id;
        ELSE
            UPDATE aiep.task_runs
            SET status = 'DeadLettered', lease_owner = NULL, lease_expires_at = NULL, completed_at = now(),
                error_category = 'LeaseExpired', error_message = 'worker lease expired; attempts exhausted',
                updated_at = now()
            WHERE id = v_run.id;
        END IF;

        v_count := v_count + 1;
    END LOOP;

    RETURN v_count;
END;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Only the worker claims and reconciles. (The API submits runs and cancels them through the ordinary unit of work.)
-- ---------------------------------------------------------------------------------------------------------------
REVOKE ALL ON FUNCTION aiep.claim_next_run(text, uuid)       FROM PUBLIC;
REVOKE ALL ON FUNCTION aiep.reconcile_expired_leases(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION aiep.claim_next_run(text, uuid)       TO aiep_worker;
GRANT EXECUTE ON FUNCTION aiep.reconcile_expired_leases(integer) TO aiep_worker;
