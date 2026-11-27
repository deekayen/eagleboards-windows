# 0004. Log what a diagnosis needs, and never a name

Status: accepted (2026-11-27)

## Context

At the 2026-11-17 event, adults who had signed in at a station didn't appear
in the window for minutes. The event's `eagleboards.log` had ten lines: two
starts on different network addresses, two SignUpGenius imports, and two
check-in addresses. It couldn't say whether a station's sign-in reached the
scheduler, when, or what it did; whether the window was slow, frozen, or never
told; whether the laptop's network changed or it went to sleep; or how many
people were at the event. Sign-ins were written only with `-v`, and then with
every field, names, emails, and phones included.

The log sits in the event's folder, beside files that hold names. It is also
what the owner can share, with a contributor or an AI assistant, after
something goes wrong, so what it holds should be safe to share.

## Decision

The app writes these lines to the event's log, whatever the verbose setting:

- Each request a station makes for a page or an endpoint, once answered:
  method, path, the station's address, status, and milliseconds
  (`CheckInServerOptions.LogRequests`). Not the query or the form, and not a
  page's stylesheet, script, or pictures.
- What each sign-in did (`BoardService`): an adult new to the scheduler,
  known from an earlier event, or signing in again, and whether that leaves
  them marked gone home or on a board; a youth's sign-in number. With the
  count of adults, or of youth waiting.
- When the window falls behind (`ResponsivenessLog`): a change shown a second
  or more after it was made, a refresh that took a second or more, and the
  minute timer running 15 seconds or more late, which means the window's
  refresh was held up as long. Every five minutes, the event's size in counts.
- The computer (`ComputerWatch`): its network addresses at start and
  whenever they change, saying when the check-in website's address is no
  longer one of them; going to sleep and waking.
- `--- stopping` before the session's first await on the way out, since
  `App.OnExit` can't wait for `--- stopped`. Exceptions that end the process
  or were never observed.

None of these lines holds a name, email, phone, unit, or anything a volunteer
typed. They give counts, sign-in numbers, and room names. Two kinds of line
can hold more, as before: an exception's text (an error or a crash), which is
written as .NET gives it and can quote a file path or a value, and everything
`-v` writes, which is the full records, for a practice run.

The headless `EagleBoards.Server` writes request lines only with `-v`, as the
Java server does, so its console output stays as it was. It writes the
sign-in lines like the app.

## Consequences

- The next report like 2026-11-17's can be read from the log: whether the
  station's request arrived and when, what it did, and whether the window
  showed it late or the network went away.
- A busy event writes a few hundred more lines. At under a hundred bytes each
  that is tens of kilobytes.
- A line about a particular person is found by its time, its station, and
  whether it was a new adult, not by name. That is enough to match what an
  operator remembers ("about 7:15, at the second tablet").
- The Java and Mac versions log differently. This is about diagnosing the
  Windows app, not the event's rules, so it is not a `SPEC.md` item; one could
  be written if the other versions want the same lines.
