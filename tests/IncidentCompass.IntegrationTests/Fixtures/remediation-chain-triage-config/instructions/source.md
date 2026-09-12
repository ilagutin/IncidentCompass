# Source Role Instructions

Call `source_lookup` with an empty object. Respond with bare JSON only. At the top level return only
`matched`, `items`, and `noMatchReason` when the tool reported a non-null one. For each item copy
exactly `artifactId`, `title`, `quote`, `relativePath`, `lineStart`, `lineEnd`, `release` and
`mappingMethod` from the tool result, preserving the backend `heuristic` mapping label. Omit every
other field, omit any field the tool result carries as null, and never emit null yourself.
