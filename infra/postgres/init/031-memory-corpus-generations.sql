-- v0.4 memory corpus generations: what vector space the active seed corpus is actually in.
--
-- Before this table the system recorded a generation identifier on every active seed item and
-- nothing else. That answered "which pass published this row" and left the question that decides
-- whether retrieval works at all unanswered: which embedding route produced these vectors.
--
-- The gap had a specific failure. `memory_search` filters candidate chunks by the query
-- embedding's provider, model and dimensions, so a corpus embedded under one model stops matching
-- the moment the configured model changes. The seed pass would not notice, because it decides
-- whether to re-embed a file by comparing the file's content hash, and a model change does not
-- edit a file. The result was a corpus that was fully present, fully active and returned nothing,
-- with a healthy status beside it.
--
-- The table below is written inside the same transaction that publishes the corpus, so a
-- generation becomes current only after every embedding and every item and chunk write for it has
-- succeeded. The partial unique index is the invariant: one current generation per owner and
-- tenant, enforced by the database rather than by a convention in one code path.
--
-- `route_id` and `provider_id` are the configured names a route is known by, not an endpoint and
-- never a credential. They are here because the adapter identity in `memory_chunks` cannot tell
-- two configured providers apart: every OpenAI-compatible provider reports the same
-- `embedding_provider` string, so a configuration moved from one embedding server to another with
-- the same model name is invisible in the chunk row. Both columns are nullable because a corpus
-- published before this migration was published by a pass that recorded neither, and inventing a
-- value for it would assert something never observed.

CREATE TABLE IF NOT EXISTS incidentcompass.memory_corpus_generations (
    generation uuid PRIMARY KEY,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    seed_owner text NOT NULL CHECK (length(btrim(seed_owner)) > 0),
    route_id text NULL CHECK (route_id IS NULL OR length(btrim(route_id)) BETWEEN 1 AND 256),
    provider_id text NULL CHECK (provider_id IS NULL OR length(btrim(provider_id)) BETWEEN 1 AND 256),
    embedding_provider text NOT NULL CHECK (length(btrim(embedding_provider)) > 0),
    embedding_model text NOT NULL CHECK (length(btrim(embedding_model)) > 0),
    embedding_dimensions integer NOT NULL CHECK (embedding_dimensions > 0),
    item_count integer NOT NULL CHECK (item_count >= 0),
    chunk_count integer NOT NULL CHECK (chunk_count >= 0),
    is_current boolean NOT NULL,
    published_at_utc timestamptz NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_corpus_generations_current
    ON incidentcompass.memory_corpus_generations (tenant_id, seed_owner)
    WHERE is_current;

CREATE INDEX IF NOT EXISTS ix_memory_corpus_generations_scope
    ON incidentcompass.memory_corpus_generations (tenant_id, seed_owner, published_at_utc DESC);

-- Backfill for a database that already holds a seeded corpus. Only an owner whose active seed
-- items all carry one generation and whose active chunks all sit in one vector space is recorded:
-- anything else is genuinely ambiguous, and guessing which of two spaces is current would be the
-- silent mixing this table exists to make visible. An unrecorded owner is reported as such and is
-- resolved by running the rebuild, which records the full identity from real embedding responses.
DO $migration$
DECLARE
    scope record;
    identity_count integer;
    resolved_generation uuid;
BEGIN
    FOR scope IN
        SELECT item.tenant_id AS tenant_id, item.seed_owner AS seed_owner
        FROM incidentcompass.memory_items AS item
        WHERE item.seed_managed = true
          AND item.is_active = true
          AND item.seed_generation IS NOT NULL
        GROUP BY item.tenant_id, item.seed_owner
        HAVING count(DISTINCT item.seed_generation) = 1
    LOOP
        CONTINUE WHEN EXISTS (
            SELECT 1
            FROM incidentcompass.memory_corpus_generations AS existing
            WHERE existing.tenant_id = scope.tenant_id
              AND existing.seed_owner = scope.seed_owner
              AND existing.is_current);

        SELECT count(*)
        INTO identity_count
        FROM (
            SELECT DISTINCT chunk.embedding_provider, chunk.embedding_model, chunk.embedding_dimensions
            FROM incidentcompass.memory_chunks AS chunk
            JOIN incidentcompass.memory_items AS item ON item.id = chunk.memory_item_id
            WHERE item.tenant_id = scope.tenant_id
              AND item.seed_owner = scope.seed_owner
              AND item.seed_managed = true
              AND item.is_active = true
        ) AS identities;

        CONTINUE WHEN identity_count <> 1;

        SELECT DISTINCT item.seed_generation
        INTO resolved_generation
        FROM incidentcompass.memory_items AS item
        WHERE item.tenant_id = scope.tenant_id
          AND item.seed_owner = scope.seed_owner
          AND item.seed_managed = true
          AND item.is_active = true;

        INSERT INTO incidentcompass.memory_corpus_generations (
            generation, tenant_id, seed_owner, route_id, provider_id,
            embedding_provider, embedding_model, embedding_dimensions,
            item_count, chunk_count, is_current, published_at_utc)
        SELECT
            resolved_generation,
            scope.tenant_id,
            scope.seed_owner,
            NULL,
            NULL,
            min(chunk.embedding_provider),
            min(chunk.embedding_model),
            min(chunk.embedding_dimensions),
            count(DISTINCT item.id)::integer,
            count(*)::integer,
            true,
            max(item.updated_at_utc)
        FROM incidentcompass.memory_chunks AS chunk
        JOIN incidentcompass.memory_items AS item ON item.id = chunk.memory_item_id
        WHERE item.tenant_id = scope.tenant_id
          AND item.seed_owner = scope.seed_owner
          AND item.seed_managed = true
          AND item.is_active = true
        HAVING count(*) > 0
        ON CONFLICT (generation) DO NOTHING;
    END LOOP;
END $migration$;
