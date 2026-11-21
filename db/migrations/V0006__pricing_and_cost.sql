-- V0006: pricing and cost (Phase 4). A global, versioned pricing table (ADR-0009), and per-run cost columns. Costs are
-- computed by the worker from usage and the pricing row in effect, and record which pricing row they used.
-- Applied by Aiep.Migrator inside a single transaction; do not add BEGIN/COMMIT here.

-- ---------------------------------------------------------------------------------------------------------------
-- model_pricing: global reference data, not tenant-owned (no organization_id, no RLS). Services read it; only a
-- migration writes it. Prices are USD per 1,000,000 tokens. A model's price history is its rows ordered by
-- effective_from; a cost records the exact row (pricing_id) it used.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE aiep.model_pricing (
    id                     uuid         PRIMARY KEY,
    provider               text         NOT NULL CHECK (provider ~ '^[a-z0-9_-]{1,40}$'),
    model                  text         NOT NULL CHECK (length(btrim(model)) BETWEEN 1 AND 100),
    effective_from         timestamptz  NOT NULL,
    input_price_per_mtok   numeric(12, 4) NOT NULL CHECK (input_price_per_mtok >= 0),
    output_price_per_mtok  numeric(12, 4) NOT NULL CHECK (output_price_per_mtok >= 0),
    source_url             text         NULL CHECK (length(source_url) <= 500),
    created_at             timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT model_pricing_provider_model_effective_uk UNIQUE (provider, model, effective_from)
);

-- Seed the two fake profiles used for the evaluation comparison (deterministic, no real spend). fake-sonnet is the
-- higher-quality, higher-cost profile; fake-haiku is cheaper. Real-provider rows land with the Anthropic adapter.
INSERT INTO aiep.model_pricing (id, provider, model, effective_from, input_price_per_mtok, output_price_per_mtok, source_url)
VALUES
    ('01900000-0000-7000-8000-000000000001', 'fake', 'fake-sonnet', '2026-01-01T00:00:00Z', 3.0000, 15.0000, 'https://aiep.local/pricing/fake'),
    ('01900000-0000-7000-8000-000000000002', 'fake', 'fake-haiku',  '2026-01-01T00:00:00Z', 0.8000,  4.0000, 'https://aiep.local/pricing/fake');

-- Services read pricing; nobody but a migration writes it.
REVOKE INSERT, UPDATE, DELETE ON aiep.model_pricing FROM aiep_api, aiep_worker;

-- ---------------------------------------------------------------------------------------------------------------
-- Per-run cost. Stored on the attempt (what that attempt cost) and copied to the run for the winning attempt.
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE aiep.task_run_attempts
    ADD COLUMN cost_usd   numeric(12, 6) NULL CHECK (cost_usd >= 0),
    ADD COLUMN pricing_id uuid           NULL REFERENCES aiep.model_pricing (id);

ALTER TABLE aiep.task_runs
    ADD COLUMN cost_usd   numeric(12, 6) NULL CHECK (cost_usd >= 0),
    ADD COLUMN pricing_id uuid           NULL REFERENCES aiep.model_pricing (id);
