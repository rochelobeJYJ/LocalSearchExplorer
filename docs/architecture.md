# Architecture

```text
LocalSearchExplorer
 ├─ LocalSearch.App
 │  └─ WPF search UI, Explorer-like result actions, index controls
 └─ LocalSearch.Core
    ├─ Data: SQLite schema and repositories
    ├─ Indexing: folder scanner and metadata indexer
    ├─ Search: tokenizer, parser, AST evaluator, ranking
    └─ Content: local-only text extraction and content indexing
```

The default database path is `%LOCALAPPDATA%\LocalSearchExplorer\index.db`.

Main tables:

- `roots`: user-selected search roots
- `items`: file and folder metadata
- `content_index`: extracted local document text
- `exclusions`: folder or path patterns excluded from indexing
- `extraction_errors`: content extraction failures
- `duplicate_candidates`: reserved for future duplicate workflows

Indexing flow:

1. User adds a root folder.
2. The scanner walks folders recursively.
3. Exclusion rules are applied before indexing.
4. Metadata is replaced for that root in SQLite.
5. If content search is enabled, supported file text is extracted and indexed locally.
6. Failures are recorded without stopping metadata indexing.

Search flow:

1. Query text is tokenized.
2. Tokens are parsed into an AST.
3. SQLite supplies indexed metadata and optional content text.
4. The evaluator applies fields, operators, filters, and ranking.
5. Results are sorted by score, modified date, and name.

No telemetry, sync, remote search, or external server calls are used by the app.
