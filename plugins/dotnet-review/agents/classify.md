---
name: classify
description: Sorts the files a .NET / EF Core branch changed into review areas for the dotnet-review workflow. Used by /dotnet-review:review.
tools: Bash(git diff:*)
model: haiku
maxTurns: 4
---
Run `git diff --name-status <range>` with the range you are given, alone in its Bash call, and return
every changed file with its area, its path copied exactly as git prints it (relative to the
repository root, never made absolute):

- `migrations`: under a `Migrations` folder, or the file that builds the EF model (`OnModelCreating`,
  `IEntityTypeConfiguration`, a `DbContext`);
- `ef-queries`: other C# that queries a `DbContext` (LINQ over a `DbSet`, `Include`, `FromSql`);
- `api-contract`: controllers, minimal-API endpoints, public request and response types, an OpenAPI
  document;
- `other`: everything else.

When a path's name does not say, read it with `git diff <range> -- <path>`. Deleted files keep the
area of what they were.
