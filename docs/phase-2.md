# Phase 2: Metadata

Phase 2 builds one metadata pipeline shared by local `ComicInfo.xml`, ComicVine lookup, and the metadata review interface.

## Milestones

1. Read root-level `ComicInfo.xml` safely and merge it over filename-derived defaults.
2. Add ComicVine lookup with rate limiting, caching, and user-supplied API credentials.
3. Add a metadata review interface that shows source, confidence, conflicts, and editable final values.
4. Persist reviewed metadata and queue state for restart recovery.

The first milestone deliberately keeps `ComicMetadata` as the canonical conversion input so later providers do not couple network or UI concerns to EPUB generation.
