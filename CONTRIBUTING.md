# Contributing to ZeyoCut

Thank you for helping. Bug reports, fixes, documentation and new tools are all welcome. For anything bigger than a small fix, please open an issue first, so we can agree on the approach before you spend time on it.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Set up

You need Windows 10 or 11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the full test suite you also need:

- **FFmpeg** on `PATH` or in `FFMPEG_PATH` (`winget install Gyan.FFmpeg`). Without it, the tests that need it are reported as skipped.
- **whisper.cpp** for the transcript tests: set `WHISPER_PATH` to `whisper-cli.exe` or its folder, with a ggml model next to it. Without it, those tests skip.
- **Windows text-to-speech**, which the tests use to make spoken test audio, and the fonts **Myanmar Text** and **Yu Gothic**. All ship with Windows; tests skip when they are missing.

Test media is generated at test time. The only media files are three public-domain photos for the face and plate detection tests, listed in [tests/ZeyoCut.Tests/Fixtures/README.md](tests/ZeyoCut.Tests/Fixtures/README.md); please don't add others without a clear license.

## Build and test

```powershell
dotnet build
dotnet test
```

The full suite takes a few minutes. To run part of it, use the test exe directly, with either `-class` or `-method`, not both:

```powershell
tests\ZeyoCut.Tests\bin\Debug\net10.0\ZeyoCut.Tests.exe -class "ZeyoCut.Tests.Core.MarkerTests"
tests\ZeyoCut.Tests\bin\Debug\net10.0\ZeyoCut.Tests.exe -method "*ServerChecksTheSystem"
```

To try your build in a client, point it at `src\ZeyoCut.Mcp\bin\Debug\net10.0\ZeyoCut.Mcp.exe`. Don't use `dotnet run` as the command: its build output goes to stdout and corrupts the protocol stream.

## Code style

- Match the code around you. `.editorconfig` sets the basics (LF line endings, 4 spaces in C#, 2 in JSON and YAML), and CI checks both of these:

  ```powershell
  dotnet format whitespace ZeyoCut.slnx --verify-no-changes
  dotnet format style ZeyoCut.slnx --verify-no-changes
  ```

- Every `.cs` file starts with `// SPDX-License-Identifier: MIT` and a blank line.
- Comments say why, not what.
- Rendering stays deterministic: CPU only, with no dependence on timing or thread order. Tests compare pixels.
- Processes are started with an argument list and `UseShellExecute = false` (see `ProcessRunner`), never through a shell.
- No network access, except downloading an AI model from a pinned URL and checking its pinned SHA-256. No telemetry.

## Adding or changing a tool

The tools are what the model sees, so their descriptions matter as much as their code.

1. Put the logic in `src/ZeyoCut.Core/Editing` (or `src/ZeyoCut.Render` if it draws or runs FFmpeg), where it can be tested without the server. Add tests in `tests/ZeyoCut.Tests`.
2. Add the tool to the fitting class in `src/ZeyoCut.Mcp/Tools`, with `[McpServerTool(Name = "snake_case_name")]` and a `[Description]` on the method and on every parameter. Write descriptions for the model: what the tool does, what it changes, the units, the defaults, and what to call next.
3. Wrap the body in `Guard(() => …)`. An edit goes through `state.Session.Mutate("label", p => …)`, which validates the whole project, saves it, makes a backup and adds one undo step; a read uses `state.Session.Read`. Report problems by throwing `ZeyoCutException` with a message that says how to fix them.
4. Pass every path from the client through `AllowedFolders.Readable` (files to read) or `AllowedFolders.Project` (project folders).
5. Reply with what changed, in plain text, and with `WithWarnings` for anything the user should know.
6. Add the tool's name to `ExpectedTools` in `McpServerTests`, and test it over MCP there.
7. Add a section with an example call and its real response to [docs/GUIDE.md](docs/GUIDE.md).
8. Write [docs/TOOLS.md](docs/TOOLS.md) again from the server; the test fails until it matches:

   ```powershell
   $env:ZEYOCUT_UPDATE_DOCS = "1"
   tests\ZeyoCut.Tests\bin\Debug\net10.0\ZeyoCut.Tests.exe -method "*TheToolReferenceMatchesTheServer"
   Remove-Item Env:ZEYOCUT_UPDATE_DOCS
   ```

9. Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md).

A change to `project.json` needs a new format version: raise `Project.CurrentFormatVersion`, add a step to `ProjectMigrations` (even an empty one), and test that an older file still opens.

## Dependencies

Please ask in an issue before adding a package. A new dependency must have a permissive license (MIT, Apache-2.0, BSD or similar); GPL, LGPL and AGPL code can't be used. Add each new package to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): a test fails when a package the server ships with isn't listed. Never commit AI models, fonts or media files; models are downloaded on first use.

## Sign your commits (DCO)

Every commit must be signed off under the [Developer Certificate of Origin](https://developercertificate.org/): a statement that you wrote the change, or otherwise have the right to submit it under the project's MIT license. Sign off by committing with `-s`:

```powershell
git commit -s -m "Add markers to the preview timeline"
```

This adds a line with the name and email of your git config:

```text
Signed-off-by: Your Name <you@example.com>
```

It must match the commit's author. CI checks every commit in a pull request, except Dependabot's dependency updates. To sign off commits you already made, run `git rebase --signoff main` and force-push your branch.

## Pull requests

- Keep each pull request to one change, and explain why it's needed.
- The build, the tests and both format checks must pass.
- Update the docs and the CHANGELOG with the change.
- Pictures help: a `render_frame` or contact sheet before and after, for anything you can see.

Releases are made by the maintainers; [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) lists the steps.
