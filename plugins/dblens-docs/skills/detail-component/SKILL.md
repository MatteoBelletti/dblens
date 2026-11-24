---
name: detail-component
description: Writes the detail of one component of a .NET solution: asks which component, starts its document with dblens-docs, and has its narrative written from the code and reviewed against it. Use when the user wants to understand one component of the solution in depth after reading the overview.
argument-hint: "[component=<name>]"
---

# The detail of one component

The overview (`docs/project/overview.md`) shows the components and the flows. This skill goes into one of them.

## 1. Which component

Call `get_project_inventory` with `kind` Component and show the user the services and applications (not the shared
libraries) with one line each: its name and what it publishes or consumes, from the `Event` items. Ask with
AskUserQuestion which one, unless the request already names it (`component=<name>`). Recommend the one with the most
events or endpoints.

## 2. Start its document

Call `get_doc_slots` with `state` all and `idPrefix` `detail:<name>`. If it lists the slot, the document exists: go to step 3.
Otherwise run `dblens-docs detail "<name>"` with Bash, alone in its call. It creates `docs/project/components/<slug>.md`
with the generated tables and one empty narrative slot, `detail:<name>`. If the command refuses, say why and stop.

## 3. Write it

Run the workflow `dblens-docs:fill-slots` with `{"batches": [["detail:<name>"]]}`: a writer agent reads the code the
tables point at and writes the page, a reviewer agent reads each claim against the code, and what the code does not
support is rewritten once.

## 4. Report

Call `check_doc_coverage` and report in a few lines: the document's path, what the reviewer rewrote or could not support
(verbatim), and the check's errors. Never say the page is right: say what was read against the code and what was not.
