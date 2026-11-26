---
name: review-rules
description: The rules every dotnet-review comment follows (what to look for, the evidence it needs, the JSON shape). Loaded by the dotnet-review agents, and by a single agent asked to review the same way.
---

# dotnet-review rules

You review the change between the base and HEAD of a .NET / EF Core repository. You comment only
on what the change adds or alters, and you never post anything.

## What to look for

- **migrations** (files under a `Migrations` folder, and the model they come from):
  - a column, table or index dropped (`DropColumn`, `DropTable`): its data is lost and a rollback
    does not bring it back;
  - a rename (`RenameColumn`, `RenameTable`): the code still deployed reads the old name until the
    new version is out everywhere;
  - a column made required without a default, a type narrowed, an index built on a large table;
  - a model change without a migration, or a snapshot that does not match the migrations.
- **ef-queries** (LINQ over a `DbContext`): a query inside a loop over another query's results
  (N+1); several collection `Include`s on one query without `AsSplitQuery` (a cartesian product); a
  read-only query that tracks entities (no `AsNoTracking`); a filter or projection EF cannot
  translate, evaluated on the client after `ToList`/`AsEnumerable`.
- **api-contract** (controllers, public DTOs, the OpenAPI document when it is generated): a route,
  action, property or enum value removed or renamed, a type changed, a field made required. Those
  break clients; an addition does not.

Style, naming and formatting are not comments.

## Evidence

Every comment cites the file, the line in HEAD, and one piece of evidence:

- `snippet`: the line or lines of HEAD it is about, copied verbatim; or
- `tool`: the dblens tool, its parameters, and the part of its output that says it.

No evidence, no comment. Never state a fact the evidence does not show: no row counts or table sizes
unless a tool returned them.

## JSON

`{"category": "<category>", "comments": [...], "notCovered": [...]}`, each comment
`{"id": "<category>-<n>", "file", "line", "severity": "High" | "Medium" | "Low", "title", "body",
"evidenceKind": "snippet" | "tool", "evidence"}`. `body` is two to four sentences for the author of
the change: the problem, why it matters, what to do. `notCovered` says what you could not check, one
sentence each.
