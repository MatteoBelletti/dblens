# DbLens.Docs — M1: the inventory

Plan: [DbLens.Docs — Piano](https://claude.ai/artifact/Q6e44ur3ADHDfb4LHeSQ9k). M1 builds `ProjectInventory`
on what [M0](docs-m0.md) measured. Exit criterion: the same JSON on every run over the same files, with
tests on eShop and DemoShop fixtures.

## What it lists

| Kind | Id | Read from |
|---|---|---|
| Project | `project:<name>` | the `.csproj` as XML; central versions and inherited frameworks from the nearest `Directory.Packages.props` / `Directory.Build.props` |
| Entity | `entity:<Context>.<Entity>` | the EF snapshots, through the existing `SnapshotReader` |
| Endpoint | `endpoint:<VERB> <route>` | minimal APIs (`MapGet`…, `MapMethods`, through `MapGroup` variables, versioned builders and extension methods) and controllers (`[Route]`, `[Http*]`) |
| Configuration | `config:<Key:Sub>` | `GetSection`, `GetRequiredSection`, `GetValue`, `GetConnectionString`, an indexer on a configuration, and the **key names** of `appsettings*.json` |
| HTTP client | `http:<name>` | `AddHttpClient` (type or name), with the base address when it is written |
| Background service | `hosted:<class>` | classes derived (directly or through the solution's other classes) from `BackgroundService`, or implementing `IHostedService`, with whether `AddHostedService` registers them |

`Evidence` is `Extracted` (the code states it) or `Unresolved` (the element is there, a part of it is not
written as text; the item's `reason` says which). What cannot be read at all goes in `coverage`, so an
empty inventory is never an all-clear.

## Decisions

- **Stable ids.** No line number is part of an id. Two declarations of the same verb and route (eShop's
  Catalog API v1 and v2) are told apart by the handler's name (`endpoint:GET /api/catalog/items
  [GetAllItemsV1]`), the same handler twice by an ordinal; an endpoint whose route is not text is keyed by
  its file and handler, always, so adding an action to a file never renames its neighbours.
  The same client type registered in two projects is told apart by project.
- **Unresolved is kept as an item.** The 14 conventional-routing actions of eShop's Identity.API each have
  their place in the document, with the reason, instead of disappearing into a count.
- **Conventional routing is not inferred in M1.** `MapDefaultControllerRoute()` makes the route derivable
  (`/{controller}/{action}`), but the inventory states only what the code writes; a later milestone may
  add it as `Inferred`, with a confidence.
- **No value of a settings file ever enters the inventory**, only its keys.
- **A builder handed to a method is followed to its call sites.** `MapCatalogApi(this IEndpointRouteBuilder app)`
  takes the prefix of every `x.MapCatalogApi()` and `Class.Map(group)` in the files read, when they agree;
  a method called from nowhere in them is unresolved (its group is not known). The exception is a
  parameter typed `WebApplication`, always the root. A group variable is the nearest declaration before the
  use, so two variables of the same name in one method keep their own prefix.
- **Two project files in one folder** share it: the first by path owns the source files there. Two
  projects of the same name in different folders are told apart by folder in the id.
- **Tests are not scanned** (a path segment `tests`, `test`, `e2e`, or ending `.Tests`/`.UnitTests`/…),
  and the coverage says how many files were left out. Test *projects* are still projects.
- **One I/O class.** `SolutionReader` reads the folder; the engine takes files and EF models, so it is
  tested with a few lines of source and no disk.

## What the tests hold

| Test | Holds |
|---|---|
| `EndpointInventoryTests` | named attribute arguments, builders passed as arguments, groups sharing a name, routes through groups, versioned builders, extensions called from top-level statements, constants, `MapMethods`, controllers (tokens, absolute templates, `[NonAction]`), conventional routing, id collisions and stability, what is only counted |
| `HostingInventoryTests` | configuration keys and sections, settings files without values, clients, background services, cycles |
| `ProjectInventoryTests` | SDK, output type, frameworks, references, central versions, what MSBuild would resolve, invalid XML, no project |
| `InventoryFixtureTests` | eShop: 28 projects, 19 entities, 45 endpoints (31 extracted, 14 unresolved), 4 clients, 3 background services; DemoShop's two contexts |
| `InventoryDeterminismTests` | the same bytes on every run, whatever order the files arrive in, no clock, no absolute path, and the two reference files |
| `SolutionReaderTests` | only the needed kinds of file opened, build folders skipped, a large file and a link not read, a bad snapshot in coverage |

## Limits, beyond M0's

- Configuration receivers are recognised by name (an identifier or member containing "config", not
  "configure"); a service named `appConfigService` would be read as a configuration.
- A key or a route taken from a constant of **another** file is unresolved; one from the same file resolves.
- `AddHostedService` through `AddSingleton<IHostedService>(…)` is not seen: `registeredWithAddHostedService`
  says exactly that, not "is never started".
- `http:` base addresses are literal text only; one built from a variable or a call is `none`.
