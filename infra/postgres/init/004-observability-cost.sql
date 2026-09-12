-- DORMANT: reserved for model cost rollup work.
CREATE TABLE IF NOT EXISTS incidentcompass.ai_model_pricing (
    id uuid PRIMARY KEY,
    provider text NOT NULL CHECK (length(btrim(provider)) > 0),
    model text NOT NULL CHECK (length(btrim(model)) > 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    input_token_price_per_million numeric(18, 8) NOT NULL CHECK (input_token_price_per_million >= 0),
    output_token_price_per_million numeric(18, 8) NOT NULL CHECK (output_token_price_per_million >= 0),
    embedding_token_price_per_million numeric(18, 8) NULL CHECK (embedding_token_price_per_million IS NULL OR embedding_token_price_per_million >= 0),
    effective_from_utc timestamptz NOT NULL,
    effective_to_utc timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (effective_to_utc IS NULL OR effective_to_utc > effective_from_utc)
);

CREATE INDEX IF NOT EXISTS ix_ai_model_pricing_effective
    ON incidentcompass.ai_model_pricing (provider, model, effective_from_utc DESC, effective_to_utc);

INSERT INTO incidentcompass.ai_model_pricing (
    id, provider, model, currency, input_token_price_per_million,
    output_token_price_per_million, embedding_token_price_per_million,
    effective_from_utc, effective_to_utc)
VALUES
    ('00000000-0000-0000-0000-000000000501', 'mock', 'mock-chat', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000502', 'mock', 'mock-cheap', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000503', 'mock', 'mock-strong', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000504', 'mock', 'mock-evaluation', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000505', 'mock', 'mock-chat-evaluation', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL)
ON CONFLICT (id) DO NOTHING;
