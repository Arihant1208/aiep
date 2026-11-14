-- V0002: task execution (Phase 2).
-- Tables: task_definitions, task_definition_versions, task_runs, task_run_attempts, idempotency_keys.
-- Rules: docs/data/data-model.md, ADR-0005 (PostgreSQL work queue), ADR-0006 (isolation), ADR-0007 (handlers),
--        ADR-0013 (migrations), docs/architecture/task-lifecycle.md (run states and the delivery contract).
--
-- Every tenant-owned table here follows the V0001 ownership pattern: organization_id NOT NULL, UNIQUE (organization_id, id),
-- composite foreign keys so a child can never point across tenants, and RLS ENABLE + FORCE with the tenant_isolation
-- policy. Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- ---------------------------------------------------------------------------------------------------------------
-- task_definitions: a workspace's named task. Stable identity; current_version points at the latest published version
-- number. Archive is a status change, never a delete.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.task_definitions (
    id               uuid        PRIMARY KEY,
    organization_id  uuid        NOT NULL,
    workspace_id     uuid        NOT NULL,
    slug             text        NOT NULL CHECK (slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'),
    name             text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    description      text        NULL     CHECK (length(description) <= 2000),
    status           text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'archived')),
    current_version  integer     NULL     CHECK (current_version >= 1),
    archived_at      timestamptz NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES aiep.subjects (id),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT task_definitions_org_id_uk      UNIQUE (organization_id, id),
    CONSTRAINT task_definitions_org_ws_id_uk   UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT task_definitions_org_ws_slug_uk UNIQUE (organization_id, workspace_id, slug),
    CONSTRAINT task_definitions_archived_at_ck CHECK ((status = 'archived') = (archived_at IS NOT NULL)),
    CONSTRAINT task_definitions_workspace_fk
        FOREIGN KEY (organization_id, workspace_id) REFERENCES aiep.workspaces (organization_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- task_definition_versions: immutable once published (ADR-0007: handler_type is an allowlisted platform handler).
-- A version is created as a draft, edited, then published, which stamps published_at and lets the parent definition's
-- current_version point at it. The trigger below blocks any UPDATE after publish; the publishing UPDATE itself is
-- allowed because OLD.published_at is still NULL at that point.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.task_definition_versions (
    id                  uuid        PRIMARY KEY,
    organization_id     uuid        NOT NULL,
    workspace_id        uuid        NOT NULL,
    task_definition_id  uuid        NOT NULL,
    version             integer     NOT NULL CHECK (version >= 1),
    status              text        NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'published')),
    input_schema        jsonb       NOT NULL,
    output_schema       jsonb       NOT NULL,
    handler_type        text        NOT NULL CHECK (handler_type ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'),
    handler_version     text        NOT NULL CHECK (handler_version ~ '^[0-9]+(\.[0-9]+){0,2}$'),
    model_provider      text        NOT NULL CHECK (model_provider ~ '^[a-z0-9_-]{1,40}$'),
    model_name          text        NOT NULL CHECK (length(btrim(model_name)) BETWEEN 1 AND 100),
    model_config        jsonb       NOT NULL DEFAULT '{}'::jsonb,
    prompt_template     text        NULL     CHECK (length(prompt_template) <= 100000),
    max_attempts        integer     NOT NULL DEFAULT 3  CHECK (max_attempts BETWEEN 1 AND 10),
    timeout_seconds     integer     NOT NULL DEFAULT 60 CHECK (timeout_seconds BETWEEN 1 AND 3600),
    lease_seconds       integer     NOT NULL DEFAULT 60 CHECK (lease_seconds BETWEEN 5 AND 3600),
    published_at        timestamptz NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          uuid        NULL REFERENCES aiep.subjects (id),
    CONSTRAINT task_definition_versions_org_id_uk      UNIQUE (organization_id, id),
    CONSTRAINT task_definition_versions_org_ws_id_uk   UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT task_definition_versions_def_version_uk UNIQUE (organization_id, task_definition_id, version),
    CONSTRAINT task_definition_versions_published_ck   CHECK ((status = 'published') = (published_at IS NOT NULL)),
    CONSTRAINT task_definition_versions_definition_fk
        FOREIGN KEY (organization_id, workspace_id, task_definition_id)
        REFERENCES aiep.task_definitions (organization_id, workspace_id, id)
);

CREATE FUNCTION aiep.task_definition_versions_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.published_at IS NOT NULL THEN
        RAISE EXCEPTION 'task_definition_versions is immutable once published'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER task_definition_versions_immutable
    BEFORE UPDATE ON aiep.task_definition_versions
    FOR EACH ROW EXECUTE FUNCTION aiep.task_definition_versions_immutable();

-- ---------------------------------------------------------------------------------------------------------------
-- task_runs: one logical run and its queue state (ADR-0005). status, next_attempt_at and the lease columns ARE the
-- queue: there is no separate queue table and no outbox for dispatch. input/output are stored inline as jsonb for the
-- MVP (run_artifacts / blob storage is a later phase). error_message holds a short, non-sensitive summary only.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.task_runs (
    id                               uuid        PRIMARY KEY,
    organization_id                  uuid        NOT NULL,
    workspace_id                     uuid        NOT NULL,
    task_definition_version_id       uuid        NOT NULL,
    status                           text        NOT NULL DEFAULT 'Queued'
        CHECK (status IN ('Accepted', 'Queued', 'Running', 'RetryWait',
                          'Succeeded', 'Failed', 'Cancelled', 'DeadLettered')),
    attempt_count                    integer     NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    max_attempts                     integer     NOT NULL CHECK (max_attempts BETWEEN 1 AND 10),
    input                            jsonb       NOT NULL,
    output                           jsonb       NULL,
    error_category                   text        NULL     CHECK (error_category ~ '^[A-Za-z]+$'),
    error_message                    text        NULL     CHECK (length(error_message) <= 2000),
    next_attempt_at                  timestamptz NULL,
    lease_owner                      text        NULL     CHECK (length(lease_owner) BETWEEN 1 AND 128),
    lease_expires_at                 timestamptz NULL,
    cancel_requested_at              timestamptz NULL,
    provider_call_may_have_completed boolean     NOT NULL DEFAULT false,
    traceparent                      text        NULL     CHECK (length(traceparent) <= 200),
    replayed_from_run_id             uuid        NULL,
    started_at                       timestamptz NULL,
    completed_at                     timestamptz NULL,
    created_at                       timestamptz NOT NULL DEFAULT now(),
    created_by                       uuid        NULL REFERENCES aiep.subjects (id),
    updated_at                       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT task_runs_org_id_uk    UNIQUE (organization_id, id),
    CONSTRAINT task_runs_org_ws_id_uk UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT task_runs_version_fk
        FOREIGN KEY (organization_id, workspace_id, task_definition_version_id)
        REFERENCES aiep.task_definition_versions (organization_id, workspace_id, id),
    CONSTRAINT task_runs_replay_fk
        FOREIGN KEY (organization_id, replayed_from_run_id) REFERENCES aiep.task_runs (organization_id, id)
);

-- The claim hot path: eligible runs by time, without scanning terminal rows (ADR-0005 risk mitigation).
CREATE INDEX task_runs_claimable_idx ON aiep.task_runs (next_attempt_at)
    WHERE status IN ('Queued', 'RetryWait');
-- The reconciler: running runs whose lease has expired.
CREATE INDEX task_runs_lease_idx ON aiep.task_runs (lease_expires_at)
    WHERE status = 'Running';

-- ---------------------------------------------------------------------------------------------------------------
-- task_run_attempts: one row per execution attempt; history is never overwritten (Spec 4.4). Created when a worker
-- claims the run; ended_at/status/error/usage are filled in when the attempt closes.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.task_run_attempts (
    id                   uuid        PRIMARY KEY,
    organization_id      uuid        NOT NULL,
    workspace_id         uuid        NOT NULL,
    run_id               uuid        NOT NULL,
    attempt_number       integer     NOT NULL CHECK (attempt_number >= 1),
    status               text        NOT NULL DEFAULT 'running'
        CHECK (status IN ('running', 'succeeded', 'failed', 'lease_expired', 'cancelled')),
    worker_id            text        NOT NULL CHECK (length(worker_id) BETWEEN 1 AND 128),
    error_category       text        NULL     CHECK (error_category ~ '^[A-Za-z]+$'),
    error_message        text        NULL     CHECK (length(error_message) <= 2000),
    provider_latency_ms  integer     NULL     CHECK (provider_latency_ms >= 0),
    input_tokens         integer     NULL     CHECK (input_tokens >= 0),
    output_tokens        integer     NULL     CHECK (output_tokens >= 0),
    started_at           timestamptz NOT NULL DEFAULT now(),
    ended_at             timestamptz NULL,
    CONSTRAINT task_run_attempts_org_id_uk       UNIQUE (organization_id, id),
    CONSTRAINT task_run_attempts_run_attempt_uk  UNIQUE (organization_id, run_id, attempt_number),
    CONSTRAINT task_run_attempts_run_fk
        FOREIGN KEY (organization_id, workspace_id, run_id)
        REFERENCES aiep.task_runs (organization_id, workspace_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- idempotency_keys (ADR-0015): the same Idempotency-Key with the same payload hash, from the same caller in the same
-- workspace, returns the same run; the same key with a different payload is a 409. caller_id is the submitting subject
-- or API key id. Rows are kept until expires_at, then purged.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.idempotency_keys (
    id               uuid        PRIMARY KEY,
    organization_id  uuid        NOT NULL,
    workspace_id     uuid        NOT NULL,
    caller_id        uuid        NOT NULL,
    key_hash         bytea       NOT NULL CHECK (octet_length(key_hash) = 32),
    request_hash     bytea       NOT NULL CHECK (octet_length(request_hash) = 32),
    run_id           uuid        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    CONSTRAINT idempotency_keys_org_id_uk UNIQUE (organization_id, id),
    CONSTRAINT idempotency_keys_scope_uk  UNIQUE (organization_id, workspace_id, caller_id, key_hash),
    CONSTRAINT idempotency_keys_run_fk
        FOREIGN KEY (organization_id, workspace_id, run_id)
        REFERENCES aiep.task_runs (organization_id, workspace_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Row-level security (ADR-0006). Same policy as V0001: a missing or empty context becomes NULL and matches no rows.
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE aiep.task_definitions         ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.task_definitions         FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.task_definition_versions ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.task_definition_versions FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.task_runs                ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.task_runs                FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.task_run_attempts        ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.task_run_attempts        FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.idempotency_keys         ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.idempotency_keys         FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON aiep.task_definitions
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.task_definition_versions
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.task_runs
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.task_run_attempts
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.idempotency_keys
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

-- ---------------------------------------------------------------------------------------------------------------
-- Privileges. 01-roles.sh grants SELECT/INSERT/UPDATE/DELETE on every new table to both services; narrow that to the
-- Phase 2 roles. Both services keep SELECT on every table (the no-context isolation test reads each table as each
-- role). Nothing here is ever deleted by a service.
--
--   aiep_api    authors definitions and versions, submits runs, reads attempts, records idempotency keys.
--   aiep_worker reads definitions and versions, claims and updates runs, writes attempts.
-- ---------------------------------------------------------------------------------------------------------------
REVOKE DELETE                 ON aiep.task_definitions         FROM aiep_api;
REVOKE DELETE                 ON aiep.task_definition_versions FROM aiep_api;
REVOKE DELETE                 ON aiep.task_runs                FROM aiep_api;
REVOKE INSERT, UPDATE, DELETE ON aiep.task_run_attempts        FROM aiep_api;
REVOKE UPDATE, DELETE         ON aiep.idempotency_keys         FROM aiep_api;

REVOKE INSERT, UPDATE, DELETE ON aiep.task_definitions         FROM aiep_worker;
REVOKE INSERT, UPDATE, DELETE ON aiep.task_definition_versions FROM aiep_worker;
REVOKE INSERT, DELETE         ON aiep.task_runs                FROM aiep_worker;
REVOKE DELETE                 ON aiep.task_run_attempts        FROM aiep_worker;
REVOKE INSERT, UPDATE, DELETE ON aiep.idempotency_keys         FROM aiep_worker;

REVOKE ALL ON FUNCTION aiep.task_definition_versions_immutable() FROM PUBLIC;
