---
name: slot-rules
description: The rules for the words in a dblens-docs slot (what a purpose says, what it may rest on, how long it is). Loaded by the writer and reviewer agents.
---

# Slot rules

A slot is the one place of a generated document where words go. The table above it is generated from the
code and says what the item *is*; the slot says what it is *for*.

## What to write

- **A purpose**: what the item does for the system or its callers, in one or two sentences, in the language
  of the document (the generated headings tell it). At most the slot's `maxLines`, each line at most 300
  characters, consecutive lines, plain text. No heading, list, table, code block or HTML comment.
- Say what the code shows: the condition it checks, the state it changes, the collaborator it calls. Do not
  repeat the table (verb, route, file, packages): the reader has it.
- A project's slot says its role in the solution, read from what it contains and who references it. A
  DbContext's slot says which part of the domain it holds and which store it targets. A configuration key has
  no slot.

## What it may rest on

Only on the item's `facts` and on the source you read at its `source` (the file and line), with its
neighbours when the item delegates (a handler in another file, a base class). A name alone is not enough:
`CancelOrder` may do something else. If, after reading, you cannot say what the item is for, write nothing
for it and report why. An empty slot is a warning in a check; a wrong sentence is a lie in a document.

Never state a fact the code does not show: no performance, no business reason, no history, no "legacy", no
"deprecated". Never name a person or a ticket.

## What you must not touch

- A slot with `protected: true` holds a person's words: leave it, even when it looks wrong.
- Everything outside a slot. You have no tool that writes anywhere else, and no reason to look for one.
- Data and secrets: configuration values are not in the inventory and you must not read `appsettings*.json`
  values, `.env` files or user secrets into a sentence.
