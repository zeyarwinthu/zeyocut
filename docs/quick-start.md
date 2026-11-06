# Quick start

From download to your first video, on Windows 10 or 11. The [install guide](install.md) has more detail on every step.

## 1. Install FFmpeg

ZeyoCut uses FFmpeg for video and for every export. In PowerShell:

```powershell
winget install Gyan.FFmpeg
```

## 2. Download ZeyoCut

From the [latest release](https://github.com/zeyarwinthu/zeyocut/releases/latest), download:

- `ZeyoCut-<version>.mcpb` for Claude Desktop, or
- `ZeyoCut-<version>-win-x64.zip` for Claude Code and other MCP clients.

Check its SHA-256 against `SHA256SUMS.txt`, as [Check the download](install.md#check-the-download) shows. The exe isn't code-signed yet, so Windows may warn about it; see [Windows warnings](install.md#windows-warnings).

## 3. Connect your client

**Claude Desktop.** Open **Settings → Extensions → Advanced settings**, click **Install Extension…** and choose the `.mcpb` file.

**Claude Code.** Unzip the zip, for example to `C:\Tools\ZeyoCut`, and run:

```powershell
claude mcp add --transport stdio --scope user zeyocut -- C:\Tools\ZeyoCut\ZeyoCut.Mcp.exe
```

**Other clients.** Add `ZeyoCut.Mcp.exe` as a local (stdio) MCP server; see [Other MCP clients](install.md#other-mcp-clients).

Restart the client so it picks up FFmpeg on the `PATH`.

## 4. Make something

Start with a system check. It tells you whether FFmpeg was found, and which folders ZeyoCut can import files from:

> Run ZeyoCut's system check.

Then ask for a video. Put your files in your Pictures, Videos, Music, Documents, Downloads or Desktop folder:

> Make a 12-second comic video from the panels in my Pictures\comic folder: slow zoom into each panel, cross-fade between them, add voice.mp3 from the same folder, and subtitles in a dark box at the bottom. Export it as comic.mp4.

> Make a YouTube video from Videos\talk.mp4: cut the silences with punch-ins, remove the "um"s, add bold captions and Music\bed.mp3 under the voice, and export it with the youtube_1080p preset. Then make a vertical Short of it with the speaker centered.

> Blur every face in Videos\street.mp4, show me the frames I should check, and export it.

## Next

- [Examples](examples.md): three projects to open, with the prompts that made them, and the steps for cleaning up a talking-head video.
- [Guide](GUIDE.md): every tool, with an example call and its real response.
- [Project format](project-format.md): how projects, animation, text, video and audio work.
