# Contributing

Thanks for helping. Everything you need to change this project safely is in
[`CLAUDE.md`](CLAUDE.md): read it first. It is written for anyone, not only
for AI assistants. The short version:

- **Behaviour more than one version shares** (the board rules, the check-in
  pages, the data files, the operator screen) is decided in
  [`SPEC.md`](https://github.com/deekayen/eagleboards-shared/blob/main/SPEC.md)
  in `deekayen/eagleboards-shared`. Open an issue there before changing it
  here. Why the Windows version is built as it is: [`docs/decisions`](docs/decisions/README.md).
- **Build and test:** `dotnet build -c Release`, then
  `dotnet test --project tests/EagleBoards.Tests -c Release` and
  `dotnet test --project tests/EagleBoards.UiTests -c Release`. CI runs the same and
  more; it is what decides whether a change is accepted.
- **Never commit real people's data.** Tests and screenshots use made-up
  names only. Install the guard once per clone:
  `git config core.hooksPath scripts/hooks`.
- **Write code others can read:** see *Writing code others can read* in
  [`CLAUDE.md`](CLAUDE.md).
- **Security problems:** see [`SECURITY.md`](SECURITY.md); don't open a public
  issue.
