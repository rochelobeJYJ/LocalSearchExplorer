# Query Syntax

Local File Search Explorer supports simple search and structured search.

## Operators

| Syntax | Meaning |
|---|---|
| `A` | Match `A` in name or path. If content search is enabled, content is also searched. |
| `A & B` | Match both terms. |
| `A, B` | Match either term. |
| `!A` | Exclude `A`. |
| `-A` | Exclude `A`. |
| `"A B"` | Match a phrase. |
| `(A, B) & C` | Group expressions. |
| `*.pdf` | Match PDF extension. |
| `re:/^IMG_\d+\.jpg$/` | Regex match against name and path. |

Precedence: parentheses, NOT, AND, OR. Adjacent terms are treated as AND.

## Fields

| Syntax | Meaning |
|---|---|
| `name:보고서` | Match file or folder name. |
| `path:업무` | Match full path. |
| `ext:pdf` | Match one extension. |
| `ext:pdf,xlsx,docx` | Match any listed extension. |
| `type:file` | Files only. |
| `type:folder` | Folders only. |
| `size:>100MB` | File size greater than 100 MB. |
| `modified:today` | Modified today. |
| `modified:>=2025-01-01` | Modified on or after date. |
| `content:"계약 금액"` | Match indexed document content. |

When whitespace-ignore mode is enabled, `연차 신청서`, `연차신청서`, and `연 차 신 청 서` are matched equivalently.
