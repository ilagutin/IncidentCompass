-- Incident memory schema: seeded runbooks, known incidents and operational notes.

CREATE TABLE IF NOT EXISTS incidentcompass.memory_items (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    kind text NOT NULL CHECK (kind IN ('runbook', 'known_incident', 'operational_note')),
    source text NOT NULL CHECK (length(btrim(source)) > 0),
    title text NOT NULL CHECK (length(btrim(title)) > 0),
    content text NOT NULL CHECK (length(btrim(content)) > 0),
    content_hash text NOT NULL CHECK (length(btrim(content_hash)) > 0),
    version integer NOT NULL CHECK (version > 0),
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    created_at_utc timestamptz NOT NULL,
    UNIQUE (tenant_id, source, content_hash, version)
);

CREATE TABLE IF NOT EXISTS incidentcompass.memory_chunks (
    id uuid PRIMARY KEY,
    memory_item_id uuid NOT NULL REFERENCES incidentcompass.memory_items (id) ON DELETE CASCADE,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    chunk_position integer NOT NULL CHECK (chunk_position >= 0),
    text text NOT NULL CHECK (length(btrim(text)) > 0),
    text_hash text NOT NULL CHECK (length(btrim(text_hash)) > 0),
    embedding_provider text NOT NULL CHECK (length(btrim(embedding_provider)) > 0),
    embedding_model text NOT NULL CHECK (length(btrim(embedding_model)) > 0),
    embedding_dimensions integer NOT NULL CHECK (embedding_dimensions > 0),
    embedding_values real[] NOT NULL,
    embedding_vector vector NOT NULL,
    created_at_utc timestamptz NOT NULL,
    CHECK (cardinality(embedding_values) = embedding_dimensions),
    CHECK (vector_dims(embedding_vector) = embedding_dimensions),
    UNIQUE (memory_item_id, chunk_position, embedding_provider, embedding_model, embedding_dimensions)
);

CREATE INDEX IF NOT EXISTS ix_memory_chunks_exact_filter
    ON incidentcompass.memory_chunks (
        tenant_id,
        embedding_provider,
        embedding_model,
        embedding_dimensions);
