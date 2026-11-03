# 0003. Serve the admin endpoints only from the headless server

Status: accepted (2026-11-03)

## Context

`CheckInServer` answers two kinds of endpoint. The check-in ones
(`/register-youth`, `/api/*` and the rest) are for the stations and answer
the network. The others (`/seat-board`, `/complete-board`, `/restore-board`,
`/room-change`, `/update-config`, the `*-update` and `*-cells` endpoints) are
the ones the Java version's browser admin pages used. They answer only
requests from this machine, judged by the caller's address. (Two check-in
endpoints, the youth and adult lists, are public too, but a caller on this
machine was given every column of them, phones and emails included.)

The Windows app never calls them: it works on `BoardService` directly. They
were kept so the HTTP event test, written against the Java server, could
prove the port behaves the same.

A caller's address is not enough on the admin computer, which is also the
computer the operator browses on. Any web page open there can make the
browser send a request to `http://localhost:8080/restore-board`, and
`DispatchAsync` accepts a GET with its parameters in the query string. Nothing
checks the `Host` or `Origin` of the request.

## Decision

The admin endpoints are registered only when
`CheckInServerOptions.ServeAdminEndpoints` is true. It is false by default,
so the app serves only the check-in endpoints and every other path answers
404, even to this machine. Without it, the two public lists give this
machine names and units only, like a station. The headless
`EagleBoards.Server` sets it, as do the server tests, so
`scripts/test-board-event.sh` still drives every endpoint.

## Consequences

- The app's attack surface on the loopback address is the check-in
  endpoints, which already face the network, and no more of the lists than a
  station gets.
- The HTTP event test is unchanged: it runs against the headless server (or
  the Java jar).
- Anything that used the admin endpoints of a running exe, such as a local
  script, gets 404. Nothing in this repository did; the smoke test of the
  published exe calls only the check-in endpoints.
- If the exe ever needs one of them, add it to the check-in group on purpose,
  with its own checks, not by turning the whole set back on.
