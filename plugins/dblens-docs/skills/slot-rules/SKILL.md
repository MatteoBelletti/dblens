---
name: slot-rules
description: The rules for the words in a dblens-docs slot (what a caption says, what it may rest on, how long it is). Loaded by the writer agent.
---

# Slot rules

A slot is the one place of a generated document where words go. The table above it says what the item *is*; the slot says what it is *for*.

- Write in the `language` get_doc_slots returns (`it`, `en`), whatever the code or this skill is written in; names from the code stay as they are.
- A caption: one or two sentences on what the item does for the system or its callers, at most `maxLines` consecutive lines of 300 characters, plain text (no heading, list, table, code block or HTML comment).
- Say what the code shows: the condition it checks, the state it changes, the collaborator it calls. Do not repeat the table (verb, route, file, packages).
- A project's slot says its role in the solution, from what it contains and who references it. A DbContext's says which part of the domain it holds and which store it targets. A configuration key has no slot.

## A `detail:` slot: the page about a component

Up to 80 lines of Markdown: paragraphs, `##` headings, lists, and at most one Mermaid sequence diagram (solid arrows `->>` only: a text with `-->` is refused). The generated tables above the slot already say what exists; the page says how it works. In this order, only what the code shows:

1. What it does and for whom, in three or four lines.
2. How it works: the main flow step by step. Cite each step in backticks as `File.cs: Method` or `Type: Method` (a method, property or constructor you read): the server refuses words that cite what is not in the code, and the check flags the words when the cited code changes.
3. States and timing, if any: a status enum and its transitions, delays, schedules.
4. When something goes wrong: error paths, what the caller sees, what is retried and what is not.
5. Before touching it: surprising behaviour, silent failures, coupling to other components, each with file and method.
6. Where to change it for the usual changes.
7. What you did not check, as a short list under a heading `## Non verificato` (Italian) or `## Not verified`: a page without it is flagged. An interpretation is marked as one.

Read the code the tables point at (handlers, aggregate, background service): a name is not a description.

## What it may rest on

Only the item's `facts` and the source you read at its `source`, with its neighbours when the item delegates (a handler in another file, a base class). A name is not enough: `CancelOrder` may do something else. If, after reading, you cannot say what it is for, write nothing and report why: an empty slot is a warning, a wrong sentence is a lie in a document.

Never state what the code does not show: no performance, business reason, history, "legacy", "deprecated"; never name a person or a ticket.

## What you must not touch

- A slot with `protected: true` holds a person's words: leave it.
- Anything outside a slot, and data or secrets: never put `appsettings*.json` values, `.env` files or user secrets into a sentence.
