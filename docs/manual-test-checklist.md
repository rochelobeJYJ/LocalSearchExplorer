# Manual Test Checklist

## Metadata

- Add a sample root folder.
- Confirm files and folders appear.
- Search `연차신청서` with whitespace-ignore enabled.
- Search `계약서 & !초안`.
- Search `ext:txt`.
- Search `type:folder 업무`.
- Search `path:업무`.
- Confirm size and modified columns display.

## Content

- Enable content search before indexing or rescan after enabling it.
- Search `content:"계약 금액"` against a text document containing that phrase.
- Search a content-only term with content search off, then on.
- Confirm the content preview column is populated for content matches.

## Actions

- Double-click a result to open it.
- Press Enter on a result to open it.
- Press Ctrl+Enter to open the parent folder.
- Press Ctrl+C to copy selected paths.
- Use right-click: open, reveal in Explorer, properties, rename, delete, exclude folder, rescan folder, terminal.
- Press F5 to rescan.
- Press Ctrl+L to focus search.
- Press Esc to clear search.
- Press Alt+Enter for properties.

## Index Management

- Add an exclusion from a result folder and rescan.
- Confirm excluded files disappear from search.
- Remove a root and confirm original files remain on disk.
- Delete content index and confirm metadata search still works.
- Delete all indexes and confirm roots/results are cleared.
