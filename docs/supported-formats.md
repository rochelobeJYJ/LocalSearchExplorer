# Supported Formats

## Metadata Search

All accessible files and folders under user-selected roots are indexed by name, path, extension, type, size, created date, modified date, and attributes.

## Content Search

Content indexing is optional and local-only.

| Format | Support |
|---|---|
| TXT, MD, CSV, LOG, JSON, XML | Direct text read |
| PDF | Text PDF extraction through PdfPig |
| DOCX | OpenXML body text extraction |
| XLS, XLSX | Sheet/cell text extraction through ExcelDataReader |
| HWPX | ZIP/XML text extraction |
| ZIP | Internal filename listing |
| HWP | Recorded as unsupported unless a reliable parser is added |
| Images, OCR PDF | Not supported |

Files over 50 MB are skipped by the default content indexer and recorded as `TooLarge`.
