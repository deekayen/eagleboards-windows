# 0002. Test with xunit.v3 on the Microsoft Testing Platform

Status: accepted (2026-11-03)

## Context

The two test projects used `xunit` 2.9.3, which NuGet marks deprecated
("Legacy", replaced by `xunit.v3`). It still worked and has no known
vulnerability, but a deprecated test framework gets no fixes, and every new
contributor would meet the warning on `dotnet list package --deprecated`.

xunit.v3 test projects are executables, and on the .NET 10 SDK they run on
the Microsoft Testing Platform (MTP). The SDK refuses to run them through the
older VSTest path (`Testing with VSTest target is no longer supported by
Microsoft.Testing.Platform on .NET 10 SDK and later`), so staying on VSTest
was not an option.

## Decision

- Both test projects reference `xunit.v3` and are `OutputType` `Exe`.
- `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are removed: they
  belong to the VSTest path and MTP does not use them.
- `global.json` opts into the new runner (`"test": { "runner":
  "Microsoft.Testing.Platform" }`), as the SDK's own error message directs.
- `dotnet test` names the project with `--project` (CI, CONTRIBUTING.md,
  README.md, and CLAUDE.md). A bare path is the VSTest form and is not
  accepted in this mode.
- The window tests turn parallelism off with
  `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]`
  (the v2 `CollectionBehavior(DisableTestParallelization = true)` is
  obsolete in v3): one UI thread, one test at a time.

## Consequences

- Test code needed no other change: the same 244 unit tests and 43 window
  tests pass.
- Anything that ran `dotnet test <path>` (a script, an editor task, a local
  habit) fails with an error until it says `--project <path>`.
- Dependabot keeps `xunit.v3` current like any other NuGet package.
- The test projects are now executables, so they can also be run directly
  (`tests/EagleBoards.Tests/bin/Release/net10.0/EagleBoards.Tests.exe`).
