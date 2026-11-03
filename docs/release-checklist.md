# Release checklist

Work through this before publishing a release, and again before the first
event on a new release. Use made-up data for every step: never the live
event folder (`C:\eagleboards`), and never real names.

## Before cutting the release

- [ ] `main` has a green build on the commit to release (the release workflow
      refuses a commit without one).
- [ ] [`CHANGELOG.md`](../CHANGELOG.md) says what changed, in an operator's
      words. Move *Not yet released* under the new version's heading.
- [ ] The Help window and README describe what the release does (the room
      rule, any new menu item).
- [ ] If the data files or the check-in pages changed, the Java hand-off test
      has been run: `EB_JAR=<jar> bash scripts/test-handoff.sh`, with a jar
      built from a fresh clone of the Java project.

## Cutting it

- [ ] Run the *release* workflow (Actions > release > Run workflow), or push a
      `vYYYY.MM.DD[.N]` tag. It makes a **draft** release.
- [ ] Download `EagleBoards.exe` from the draft and check it against
      `EagleBoards.exe.sha256`: `certutil -hashfile EagleBoards.exe SHA256`.

## On a clean Windows 10 PC

CI runs on Windows Server, and volunteers' donated laptops may still run
Windows 10. The Fluent theme is only checked there by hand.

- [ ] Start the exe from an empty folder. Windows may say it protected the PC
      (the exe isn't code-signed): *More info*, then *Run anyway*. Allow the
      firewall prompt on private networks.
- [ ] The start-up window opens, a made-up event starts, and the main window
      shows in light and in dark.
- [ ] A phone or tablet on the same network opens the check-in address and
      signs in a made-up youth and a made-up adult.
- [ ] Add a final board room and a project review room. Seat a made-up youth
      for each type and complete the boards. A project review is not
      proposed the final board's room.
- [ ] Close the exe and start it again on the same folder: everything is
      still there.

## Publishing

- [ ] Publish the draft on the Releases page.
- [ ] Tell the people who run events what changed (the changelog) and which
      exe to use.
