# DbLens.Docs — M4 (what could be built) and M5 (preparation)

Plan: [DbLens.Docs — Piano](https://claude.ai/artifact/Q6e44ur3ADHDfb4LHeSQ9k).

## M4: cross-references

**Built: endpoint ↔ entity links.** An endpoint lists the entities its handler names (the handler's
signature and body: a lambda, the method it names, a controller action); an entity lists the endpoints that
name it. Both are rows of the generated tables labelled *inferred*, only where there is one.

- A heuristic over names, said so in the inventory's `coverage` ("links") with the number of endpoints that
  show none: a handler that delegates to a service, or reaches an entity through a DbSet property or `var`,
  shows nothing. The links are signals, not a complete map.
- `x.Order` (a member of something else) does not count; `Order`, `Results.Ok<Order>`, `typeof(Order)` do. A
  name two contexts share is listed with each context (`Archive.Order, Sales.Order`); a method name that two
  methods share is not resolved.
- The links are attributes of the items, so they are in the item hash: a new link makes the entity's region
  outdated (the check asks for a scaffold) and its slot stale (a warning).
- On eShop: 16 endpoints are linked to `CatalogItem`, `Order` or `WebhookSubscription`; 29 show none.

**Not built: messaging** (integration events, queues, handlers). The plan keeps this decision open: it
depends on which patterns to recognise (MediatR, MassTransit, Wolverine, a hand-written event bus…), and a
wrong guess puts false "this publishes that" lines in a document. It is named in the coverage of the
endpoints' neighbours until decided.

## M5: preparation

- `src/DbLens.Docs.Cli/README.md` is the package's readme; `dotnet pack` produces `DbLens.Docs.Cli.0.1.0`, which
  installs as a tool and runs (tried from a local source). **Nothing was published.**
- `examples/dblens-docs/shop-api`: a small API with its generated documents, words included; tests keep it
  current (`check --strict --require-slots` passes, `scaffold` changes nothing).
- Left for the user, in this order: the messaging decision; a release decision for the package (the id is
  `DbLens.Docs.Cli`, see docs-m2.md); a Trusted Publishing entry for it on nuget.org (the existing one is for
  `DbLens.Mcp`) and a release workflow; then listing the `dblens-docs` plugin in the marketplace with a
  `DbLens.Docs.Cli@<version>` pin, as the `dblens` plugin does.
