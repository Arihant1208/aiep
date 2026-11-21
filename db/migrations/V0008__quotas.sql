-- V0008: cost governance (Phase 4). A per-organization spend budget over a rolling window, enforced reserve-then-settle:
-- a run reserves an estimated cost at submission (refused with 429 if it would exceed the budget), and the worker settles
-- the actual cost when the run finishes. Threat T12. Rules follow V0001/V0002.
-- Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- ---------------------------------------------------------------------------------------------------------------
-- quota_budgets: one spend limit per scope. workspace_id NULL means the organization-wide budget. No budget row means
-- no limit. limit_usd is USD over the trailing window_seconds.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.quota_budgets (
    id               uuid        PRIMARY KEY,
    organization_id  uuid        NOT NULL REFERENCES aiep.organizations (id),
    workspace_id     uuid        NULL,
    window_seconds   integer     NOT NULL CHECK (window_seconds BETWEEN 60 AND 2678400),
    limit_usd        numeric(14, 4) NOT NULL CHECK (limit_usd >= 0),
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES aiep.subjects (id),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT quota_budgets_org_id_uk UNIQUE (organization_id, id),
    CONSTRAINT quota_budgets_workspace_fk
        FOREIGN KEY (organization_id, workspace_id) REFERENCES aiep.workspaces (organization_id, id)
);
-- At most one organization-wide budget, and at most one per workspace.
CREATE UNIQUE INDEX quota_budgets_org_wide_uk ON aiep.quota_budgets (organization_id) WHERE workspace_id IS NULL;
CREATE UNIQUE INDEX quota_budgets_org_ws_uk ON aiep.quota_budgets (organization_id, workspace_id) WHERE workspace_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------------------------
-- quota_reservations: the reserve-then-settle ledger, one row per run. estimated_usd is reserved at submit; actual_usd
-- and status='settled' are written when the run finishes. Committed spend in a window = sum(coalesce(actual, estimated)).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.quota_reservations (
    id               uuid        PRIMARY KEY,
    organization_id  uuid        NOT NULL,
    workspace_id     uuid        NOT NULL,
    run_id           uuid        NOT NULL,
    estimated_usd    numeric(12, 6) NOT NULL CHECK (estimated_usd >= 0),
    actual_usd       numeric(12, 6) NULL CHECK (actual_usd >= 0),
    status           text        NOT NULL DEFAULT 'reserved' CHECK (status IN ('reserved', 'settled')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    settled_at       timestamptz NULL,
    CONSTRAINT quota_reservations_org_id_uk UNIQUE (organization_id, id),
    CONSTRAINT quota_reservations_run_uk    UNIQUE (organization_id, run_id),
    CONSTRAINT quota_reservations_run_fk
        FOREIGN KEY (organization_id, workspace_id, run_id) REFERENCES aiep.task_runs (organization_id, workspace_id, id)
);
CREATE INDEX quota_reservations_org_window_idx ON aiep.quota_reservations (organization_id, created_at);

-- ---------------------------------------------------------------------------------------------------------------
-- RLS (ADR-0006).
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE aiep.quota_budgets      ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.quota_budgets      FORCE  ROW LEVEL SECURITY;
ALTER TABLE aiep.quota_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE aiep.quota_reservations FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON aiep.quota_budgets
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

CREATE POLICY tenant_isolation ON aiep.quota_reservations
    USING      (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('app.organization_id', true), '')::uuid);

-- ---------------------------------------------------------------------------------------------------------------
-- Privileges. The API manages budgets and reserves at submit; the worker settles the actual cost. Both keep SELECT.
-- ---------------------------------------------------------------------------------------------------------------
REVOKE DELETE ON aiep.quota_budgets FROM aiep_api;
REVOKE INSERT, UPDATE, DELETE ON aiep.quota_budgets FROM aiep_worker;
REVOKE DELETE ON aiep.quota_reservations FROM aiep_api;           -- API reserves (INSERT) and may read/adjust
REVOKE INSERT, DELETE ON aiep.quota_reservations FROM aiep_worker; -- worker only settles (UPDATE)
