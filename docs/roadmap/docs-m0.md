# DbLens.Docs — M0: the syntax spike

Plan: [DbLens.Docs — Piano](https://claude.ai/artifact/Q6e44ur3ADHDfb4LHeSQ9k). M0 asks how much of a
.NET solution projects and endpoints can be read from **syntax only** (Roslyn parses, nothing is
compiled, loaded or run), measured on `dotnet/eShop`. Gate: the resolution rate measured and the limits
written; whether to go on is decided afterwards. Run on 2026-11-24, commit `669682e` of eShop
(`Remove stored credit card data from checkout and ordering`), 547 `.cs` and 28 `.csproj` files.

The code is `spikes/docs-m0` (throwaway, not in `dblens.slnx`, replaced by `DbLens.Docs` in M1).

## What was measured

| Element | Result | How it was checked |
|---|---|---|
| Projects | 28 of 28 `.csproj` read; SDK known 28/28 | |
| `ProjectReference` | 45 of 45 resolve to an existing project file | path exists |
| `PackageReference` | 117: version in the file 16, from `Directory.Packages.props` 101, unresolved 0 | central versions need a lookup of that file |
| `OutputType` | written in 12 of 28; the other 16 follow from the SDK (`Microsoft.NET.Sdk.Web` is an executable, the rest a library) | |
| Endpoints | **44** declarations: **30 resolved**, 14 not | the denominator is independent: `grep` finds 30 `.MapGet/Post/Put/Delete/Patch(` calls and 14 `[HttpGet/Post/…]` attributes |
| Entities and tables | 4 EF snapshots (EF 8 and 11 rc) read by the existing `SnapshotReader`: 19 entities, 18 tables, 0 unsupported | `DbLens.EfCore` unchanged |
| Time | 1.2 s for the 547 files and 28 projects, JIT included | |
| Determinism | two runs give the same JSON, byte for byte | `cmp` |

The 30 resolved are every minimal API in eShop: routes built through `MapGroup` held in a variable
(`var api = app.MapGroup("api/orders")`), through a versioned-API builder (`NewVersionedApi` then
`MapGroup`, with `HasApiVersion` and the like passed through), and through an extension method whose
call site is in another file (`MapOrdersApiV1()` called from top-level statements). The routes were
read against the source for Catalog (17), Ordering (6) and Webhooks (4); none was wrong.

**The 14 unresolved are one case.** All are actions of `Identity.API`'s MVC controllers, which have no
`[Route]` and no template: their URLs come from conventional routing, and `Program.cs` calls
`MapDefaultControllerRoute()`. A first version of the spike reported them as `/`; they are now
unresolved, and the reason is named. 68% is therefore the raw ratio (30 of 44); it is 100% for the
declarations that carry their route in the code.

## Limits, to write into the inventory's `Coverage`

1. **Conventional routing.** With `MapDefaultControllerRoute()` present the route is derivable
   (`/{controller}/{action}`) but not written anywhere: it would be `Inferred`, and a custom
   `MapControllerRoute(pattern: …)` needs its pattern read. Not built in the spike.
2. **Declarations the spike does not extract** (4 kinds in eShop): `MapMethods` (1, `OPTIONS /check`),
   `MapGrpcService<T>` (1, `BasketService`: a gRPC service, not a route), `MapForwarder` (1, a YARP route
   with an explicit path), `MapRazorComponents` (2, Blazor) and `MapHealthChecks` (2, infrastructure).
   The first is trivial; the second wants its own element kind (`grpc:`); the others are listed in
   `Coverage` rather than inventoried.
3. **Route text from a constant of another class, an interpolation with a value, or a method call** is
   unresolved. A constant in the same file, and a concatenation of literals and constants, resolve.
4. **A builder passed through several layers.** Extension methods are followed to their call sites (one
   level of prefix, and the same prefix at every site); a method that receives a group from a field, a
   service or a lambda is unresolved, with its reason.
5. **Attribute-routed controllers are not exercised by eShop** (it has none). Checked on a synthetic
   controller (`spikes/docs-m0/SyntheticController.cs.txt`: `[Route("api/[controller]")]`, `{id:int}`,
   `[action]`, a same-class constant resolve; a constant of another class and an interpolation do not),
   so the path works, but its rate on a real code base is not measured.
6. **Tests are skipped** by path (`tests`, `e2e`); a repository with another layout would list them.
7. **`OutputType` and `TargetFramework`** are sometimes inherited (SDK defaults, `Directory.Build.props`):
   the inventory must say "from the SDK" rather than leave them empty.
8. **Messaging, configuration and HTTP clients** were not part of M0 (the plan puts messaging in M4).

## Reading of the result

Syntax alone is enough to inventory this solution: every project and reference, every minimal-API and
attribute route, and the data model through the code that already exists. What does not resolve falls
into one named, derivable class (conventional routing) and four declaration kinds to list or add. The
cost the plan feared, "the syntax resolves less than expected", did not materialize on eShop, and a
code base whose routes come from constants of other classes or from a library such as FastEndpoints or
Carter would resolve less: that is what `Coverage` is for. M1 should start by running its tests against
this same checkout and the `DemoShop` sample, as the plan says.

The decision to go on, and the dates, are yours.
