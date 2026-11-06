# Security policy

## Supported versions

Security fixes go into the latest release. Please check that a problem still happens with it before reporting.

## Reporting a vulnerability

Please don't report security problems in public issues, discussions or pull requests.

Report them privately through GitHub instead: on the repository's **Security** tab, click **Report a vulnerability**. Only the maintainers can see the report. Please include:

- the ZeyoCut version: the release you downloaded or the commit you built (the server log names the version when it starts)
- what an attacker could do, and what they need to have first (a crafted project, a file to import, a page in the browser, …)
- the steps to reproduce it, with a small project or file if you can
- your Windows version and MCP client

We'll confirm we got the report, keep you informed while we work on a fix, and credit you in the release notes unless you'd rather not be named. Please give us a reasonable time to release a fix before you talk about the problem in public.

## What ZeyoCut protects against

ZeyoCut runs on your computer with your permissions and does what the MCP client asks of it. These are its security boundaries; a way around any of them is a vulnerability:

- **Folders.** Projects live inside the projects folder, and files are read only from the projects folder, the library and the import folders. Paths are resolved through links and junctions before they are checked, and network paths outside those folders are refused before any disk access.
- **Project contents.** A `project.json`, a library item or an episode script can't make ZeyoCut read or write a file outside its own folders: asset, track and font files must be plain names inside their folder.
- **Processes.** FFmpeg, ffprobe and whisper are started with argument lists, never through a shell.
- **Network.** The only connections ZeyoCut makes are downloads of the AI models from their pinned URLs, each checked against a pinned SHA-256 before it is used. There is no telemetry. The preview page listens only on `127.0.0.1`, under a random token, and other sites can't read it.

Not vulnerabilities in ZeyoCut:

- What the model does with the tools inside the allowed folders, such as changing a project, when the client lets it. Most clients ask before running a tool; check what you approve.
- Problems in FFmpeg, whisper.cpp or the MCP client. Please report those to their projects; tell us too if ZeyoCut should defend against them.
- Windows' SmartScreen warnings about the unsigned exe. Check the SHA-256 of a download as [docs/install.md](docs/install.md#check-the-download) describes.
