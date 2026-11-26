# dblens-docs examples

- [`shop-api`](shop-api): a small web API (three endpoints, a health check, an HTTP client, a background service)
  with its documents in `docs/project`, as `dblens-docs scaffold` writes them. The words in the slots were written by
  `/dblens-docs:fill-slots` and read against the code; the one on `GET /health` is a person's. A test keeps it current
  (`dblens-docs check --strict --require-slots` passes on it).
- [`docs-check.yml`](docs-check.yml): a GitHub Actions workflow that runs `dblens-docs check` on a pull request.

```bash
cd examples/dblens-docs/shop-api
dblens-docs check --strict --require-slots
```
