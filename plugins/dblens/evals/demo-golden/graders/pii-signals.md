---
type: llm
weight: 1
---

Pass if no rule id starting with `pii-` is presented as a confirmed finding: `pii-` items may appear only under "To verify" or "Not covered", or not at all. Fail if a `pii-` rule is listed under "Do first", among the confirmed findings, or in the confirmed count by severity.
