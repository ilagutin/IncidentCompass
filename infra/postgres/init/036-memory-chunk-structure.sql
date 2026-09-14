ALTER TABLE incidentcompass.memory_chunks
    ADD COLUMN IF NOT EXISTS heading_path text NULL;

ALTER TABLE incidentcompass.memory_corpus_generations
    ADD COLUMN IF NOT EXISTS chunk_policy text NULL;
