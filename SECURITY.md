# Security and privacy

Eagle Boards handles personal information about **minors**. Please read this
before your first commit.

## What is sensitive

The table lists each kind of sensitive data, where it's kept, and why it
matters.

| Data | Where it lives | Why it matters |
| --- | --- | --- |
| Youth names, units, emails | the data folder's dated `YYYY-MM-DD/` folders (`scouts.csv`, `scouts_scheduled.csv`), saved reports | Personal information about minors |
| Adult names, emails, phones, board history | `adults.csv`, `Master_AdultHistory.csv` | Personal information, cumulative across years |
| SignUpGenius API key | the Windows registry of the computer running the app, encrypted for one Windows account | Grants API access to the district's sign-ups |

Eagle Boards no longer asks for, imports or keeps a youth's birthdate or phone
number (SPEC.md D-7, D-8). Folders from earlier events may still hold them:
they're left in place, and never shown, sent to a tablet or exported.

The data folder is chosen by the operator and lives outside this repository.
Nothing in the repository or in a released exe contains participant data or a
key.

## Never commit these

`.gitignore` excludes spreadsheets and CSV files, dated folders,
`Master_AdultHistory*`, `*Board_Results*`, `LOGIN_INFO*` and `.env`.
`scripts/hooks/pre-commit` refuses a commit that stages any of them, the
inherited 2019 jar, or what looks like a SignUpGenius key. Install it once per
clone:

```bash
git config core.hooksPath scripts/hooks
```

**Don't bypass it with `--no-verify`.** What it stopped can't be unpublished
once pushed: this repository is public.

The build fails if one of those files is tracked anyway, and the release
workflow refuses to publish an exe whose build holds a data file or the key.
The only committed configuration is the template `config.properties`: room
timings, no personal information.

## The API key

- Enter it in the start-up window. It's kept in the registry under
  `HKEY_CURRENT_USER\Software\Eagle Boards`, encrypted with the Windows Data
  Protection API (DPAPI) for the Windows account that saved it, so another
  account on the computer, or a copy of the registry, can't read it
  (`SignUpGeniusKeyStore`). It's never in the data folder, which the Java
  version shares, or in a `.env` file. Empty the box and start to remove it.
- On the command line, `-sugkey` takes the key, as the Java jar's does. Pass it
  from an environment variable (`-sugkey %SUG_KEY%`) rather than writing it
  into a launcher file, and remember that other programs on the computer can
  read a running program's command line.
- SignUpGenius takes the key as a URL query parameter. Eagle Boards adds it
  only as the request is sent, and never logs it or a request URL that holds
  it, even with `-v`.
- The release workflow reads it from the `SUG_KEY` repository secret, when
  there is one, only to check that the key isn't anywhere in the build. It
  never prints it.

## Network exposure

The check-in site is plain HTTP with no authentication, meant for a trusted
venue network for the length of one event.

- **Only the check-in endpoints answer the network.** The sign-in pages, the
  `/api/*` calls they make, and the sign-in calls the Java version's pages
  made answer any address. Every other endpoint (the record reads and edits,
  board actions, room changes and settings that the Java version's admin
  pages used, and its email autofill) answers only the computer running the
  app, and anyone else gets `403`. The admin pages themselves are gone (`404`): the scheduler, records
  and settings are native windows. `CheckInServerTests` checks both.
- **Responses are trimmed to what each page shows.** The lists at the door get
  names and units. An email lookup is a POST, so an address never lands in a
  URL on a shared tablet, and returns the fields its form fills in, with
  `Cache-Control: no-store` so the tablet doesn't keep them. A birthdate, or a
  youth's phone number, is never served.
- Choose the venue network in the start-up window (or pass `-bind`), so the
  site isn't served on Hyper-V, WSL or VPN adapters. Run it on the venue
  network, never on a public interface or a port-forward, and close the
  scheduler when the event ends: that stops the site.

## Working with real data

- Develop and test against synthetic data. Tests, the window tests and the
  snapshot harness make their own throwaway folders and serve on `127.0.0.1`
  only.
- Never point a test, a snapshot or a practice run at a real data folder, and
  never load the real adult history into anything you're going to screenshot.
- Saved reports and exports hold names and contact details. Keep them private.
- With `-v`, the log (`eagleboards.log`, in the event's folder) names the
  people a SignUpGenius import added. Scrub it before you share it.

## Reporting a vulnerability

Report anything that could expose participant data or the API key privately,
through GitHub's private vulnerability reporting: on this repository's
**Security** tab, select **Report a vulnerability**. Don't open a public issue
for it, and don't include the affected data in your report. Anything that
isn't sensitive can go in an ordinary issue.
