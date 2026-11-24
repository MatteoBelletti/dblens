---
name: fill-slots
description: Writes the words of the project documents that dblens-docs generated, in the slots it leaves empty or stale, each from the source of its item, reviewed against the code. Use when the user asks to fill, write or update the project documentation of a .NET solution.
argument-hint: "[idPrefix=<e.g. endpoint:>]"
---

# Fill the documentation slots

The documents are in the folder this session runs in, produced by `dblens-docs scaffold`. This skill writes
only their slots, through the `docs` MCP server, and never edits a document with Write or Edit.

## 1. What needs words

Call `get_doc_slots` (state `needed`, with `idPrefix` if the user gave one). If `total` is 0 and the note says
the documents do not exist, tell the user to run `dblens-docs scaffold` and stop. If nothing is needed, run
`check_doc_coverage` and report its result.

Show the user: "<n> slots need words: <count per kind>; <p> hold a person's words and will be left alone."
(`protected` slots are never written). Then continue, unless the user asked to be asked first.

## 2. Write them

Group the slots by `file`, at most 8 ids per batch (a `detail:` slot is a page: it goes alone in its batch), and run the workflow `dblens-docs:fill-slots` with
`{"batches": [[<ids>], …]}`. It writes, has each slot's words read against the code, and rewrites once what the
code does not support.

## 3. Check and report

Call `check_doc_coverage` with `requireSlots` true. Report in a few lines: slots written, the ones the workflow
skipped or rewrote (with their reasons, verbatim), the check's remaining errors and warnings. Never claim a
slot is right: say which were reviewed against the code and which were only rewritten.
