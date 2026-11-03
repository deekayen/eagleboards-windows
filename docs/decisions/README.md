# Decisions

Where to find why the Windows version works the way it does.

## Decisions shared by all three versions

These are in [`SPEC.md`](https://github.com/deekayen/eagleboards-shared/blob/main/SPEC.md)
in `deekayen/eagleboards-shared`, not here. Each is a numbered item that is
amended, never renumbered, and the code cites it in comments and commit
messages:

- `D-` a decision (for example D-24: a proposed board only takes a room of its
  own type),
- `O-` a decision about wording or behavior the owner settled,
- `P-` a place where the versions deliberately differ (for example P-1: the
  menu bar instead of a sidebar).

A decision that affects more than one version is made there first. Copying it
here would let the two drift apart.

## Decisions only the Windows version makes

Write one here when a choice is hard to undo or a newcomer would otherwise
ask "why is it like this?", and it is about this repository's structure or
tooling, not the event's rules. Examples: how the main window is split into
files, why the Fluent theme is still the experimental one.

One short Markdown file per decision, numbered in the order written
(`0001-split-the-main-window-into-partial-files.md`: the slug is the title), in this shape:

```markdown
# 0001. Title that states the decision

Status: proposed | accepted | superseded by 0007

## Context
What forced a choice, in a few sentences.

## Decision
What was chosen.

## Consequences
What gets easier, what gets harder, what was given up.
```

An accepted record is not edited to change the decision: write a new one that
supersedes it and update the old one's status. The format is
[MADR](https://adr.github.io/madr/) in its short form, after Michael Nygard's
[Documenting Architecture Decisions](https://www.cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

Written so far:

- [0001. Split the main window into partial files, one per part](0001-split-the-main-window-into-partial-files.md)
- [0002. Test with xunit.v3 on the Microsoft Testing Platform](0002-test-with-xunit-v3-on-the-microsoft-testing-platform.md)
- [0003. Serve the admin endpoints only from the headless server](0003-serve-the-admin-endpoints-only-from-the-headless-server.md)
