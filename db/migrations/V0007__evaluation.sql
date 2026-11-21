-- V0007: evaluation (Phase 4). Datasets (suites -> immutable versions holding examples), evaluation runs (a dataset
-- version run against a task-definition version), and per-example results linking to the executed task_runs.
-- Rules follow V0001/V0002: organization_id, composite FKs, RLS ENABLE + FORCE, tenant_isolation policy, narrowed
-- privileges. Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- ---------------------------------------------------------------------------------------------------------------
-- evaluation_suites: a workspace's named dataset. Stable identity; current_version points at the latest published one.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.evaluation_suites (
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
    CONSTRAINT evaluation_suites_org_id_uk      UNIQUE (organization_id, id),
    CONSTRAINT evaluation_suites_org_ws_id_uk   UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT evaluation_suites_org_ws_slug_uk UNIQUE (organization_id, workspace_id, slug),
    CONSTRAINT evaluation_suites_archived_at_ck CHECK ((status = 'archived') = (archived_at IS NOT NULL)),
    CONSTRAINT evaluation_suites_workspace_fk
        FOREIGN KEY (organization_id, workspace_id) REFERENCES aiep.workspaces (organization_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- evaluation_suite_versions: an immutable (once published) set of examples. examples is a JSON array of
-- {input, expected} objects; example_count is its length. The trigger blocks UPDATE after publish.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.evaluation_suite_versions (
    id               uuid        PRIMARY KEY,
    organization_id  uuid        NOT NULL,
    workspace_id     uuid        NOT NULL,
    suite_id         uuid        NOT NULL,
    version          integer     NOT NULL CHECK (version >= 1),
    status           text        NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'published')),
    examples         jsonb       NOT NULL DEFAULT '[]'::jsonb,
    example_count    integer     NOT NULL DEFAULT 0 CHECK (example_count >= 0),
    published_at     timestamptz NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES aiep.subjects (id),
    CONSTRAINT evaluation_suite_versions_org_id_uk      UNIQUE (organization_id, id),
    CONSTRAINT evaluation_suite_versions_org_ws_id_uk   UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT evaluation_suite_versions_suite_version_uk UNIQUE (organization_id, suite_id, version),
    CONSTRAINT evaluation_suite_versions_published_ck    CHECK ((status = 'published') = (published_at IS NOT NULL)),
    CONSTRAINT evaluation_suite_versions_suite_fk
        FOREIGN KEY (organization_id, workspace_id, suite_id)
        REFERENCES aiep.evaluation_suites (organization_id, workspace_id, id)
);

CREATE FUNCTION aiep.evaluation_suite_versions_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.published_at IS NOT NULL THEN
        RAISE EXCEPTION 'evaluation_suite_versions is immutable once published' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER evaluation_suite_versions_immutable
    BEFORE UPDATE ON aiep.evaluation_suite_versions
    FOR EACH ROW EXECUTE FUNCTION aiep.evaluation_suite_versions_immutable();

-- ---------------------------------------------------------------------------------------------------------------
-- evaluation_runs: one configuration (a task-definition version) evaluated against one dataset version. Fans out into
-- one task_run per example; summary is filled in when scoring completes.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.evaluation_runs (
    id                          uuid        PRIMARY KEY,
    organization_id             uuid        NOT NULL,
    workspace_id                uuid        NOT NULL,
    suite_version_id            uuid        NOT NULL,
    task_definition_version_id  uuid        NOT NULL,
    status                      text        NOT NULL DEFAULT 'Running'
        CHECK (status IN ('Pending', 'Running', 'Completed', 'Failed', 'Cancelled')),
    example_count               integer     NOT NULL CHECK (example_count >= 0),
    summary                     jsonb       NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    created_by                  uuid        NULL REFERENCES aiep.subjects (id),
    started_at                  timestamptz NULL,
    completed_at                timestamptz NULL,
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT evaluation_runs_org_id_uk    UNIQUE (organization_id, id),
    CONSTRAINT evaluation_runs_org_ws_id_uk UNIQUE (organization_id, workspace_id, id),
    CONSTRAINT evaluation_runs_suite_version_fk
        FOREIGN KEY (organization_id, workspace_id, suite_version_id)
        REFERENCES aiep.evaluation_suite_versions (organization_id, workspace_id, id),
    CONSTRAINT evaluation_runs_definition_version_fk
        FOREIGN KEY (organization_id, workspace_id, task_definition_version_id)
        REFERENCES aiep.task_definition_versions (organization_id, workspace_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- evaluation_results: one per example in an evaluation run, linking to the task_run that produced the output. score is
-- the per-scorer result (JSON object scorer -> value); filled in when the run is scored.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.evaluation_results (
    id                 uuid        PRIMARY KEY,
    organization_id    uuid        NOT NULL,
    workspace_id       uuid        NOT NULL,
    evaluation_run_id  uuid        NOT NULL,
    example_ordinal    integer     NOT NULL CHECK (example_ordinal >= 0),
    task_run_id        uuid        NOT NULL,
    score              jsonb       NULL,
    passed             boolean     NULL,
    scored_at          timestamptz NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT evaluation_results_org_id_uk    UNIQUE (organization_id, id),
    CONSTRAINT evaluation_results_run_ordinal_uk UNIQUE (organization_id, evaluation_run_id, example_ordinal),
    CONSTRAINT evaluation_results_run_fk
        FOREIGN KEY (organization_id, workspace_id, evaluation_run_id)
        REFERENCES aiep.evaluation_runs (organization_id, workspace_id, id),
    CONSTRAINT evaluation_results_task_run_fk
        FOREIGN KEY (organization_id, workspace_id, task_run_id)
        REFERENCES aiep.task_runs (organization_id, workspace_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Row-level security (ADR-0006).
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE aiep.evaluation_suites          ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_suites          FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_suite_versions  ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_suite_versions  FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_runs            ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_runs            FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_results         ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.evaluation_results         FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON aiep.evaluation_suites
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.evaluation_suite_versions
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.evaluation_runs
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.evaluation_results
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

-- ---------------------------------------------------------------------------------------------------------------
-- Privileges. The API authors suites/versions, creates and scores evaluation runs; the worker only reads (it executes
-- the fanned-out task_runs through the existing task_runs grants). Both keep SELECT; nothing is service-deletable.
-- ---------------------------------------------------------------------------------------------------------------
REVOKE DELETE ON aiep.evaluation_suites, aiep.evaluation_suite_versions, aiep.evaluation_runs, aiep.evaluation_results
    FROM aiep_api;
REVOKE INSERT, UPDATE, DELETE ON aiep.evaluation_suites, aiep.evaluation_suite_versions, aiep.evaluation_runs,
    aiep.evaluation_results FROM aiep_worker;

REVOKE ALL ON FUNCTION aiep.evaluation_suite_versions_immutable() FROM PUBLIC;
