---
type: regex
pattern: 'returned nothing|returned no verdict|unverified: [1-9]'
match: not_contains
---

Every agent ran and every finding was re-checked: a zero from an audit that did nothing is not a pass.
