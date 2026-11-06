# Install ZeyoCut

ZeyoCut runs on Windows 10 and 11 (x64). It is an MCP server with no window of its own: you use it from an MCP client such as Claude Desktop or Claude Code, and the client starts it.

## 1. Download

Get the latest release from the [Releases page](https://github.com/zeyarwinthu/zeyocut/releases):

| File | What it is |
|---|---|
| `ZeyoCut-<version>.mcpb` | The bundle for Claude Desktop, installed in a few clicks |
| `ZeyoCut-<version>-win-x64.zip` | For any MCP client: `ZeyoCut.Mcp.exe` (about 180 MB, with the .NET runtime and every library inside, so nothing else is needed to run it), `appsettings.json` and the license notices |
| `SHA256SUMS.txt` | The SHA-256 checksum of both files |

### Check the download

Put `SHA256SUMS.txt` in the same folder as the files you downloaded, open PowerShell there and run:

```powershell
foreach ($line in Get-Content .\SHA256SUMS.txt) {
  $hash, $name = $line -split '\s+', 2
  if (Test-Path $name) {
    if ((Get-FileHash $name).Hash -eq $hash) { "OK        $name" } else { "MISMATCH  $name" }
  }
}
```

For one file, `Get-FileHash .\ZeyoCut-<version>-win-x64.zip` prints its hash to compare by eye (upper or lower case doesn't matter). The same checksums are in the release notes.

If a file says `MISMATCH`, don't run it. Download it again; if it still doesn't match, please report it as described in [SECURITY.md](https://github.com/zeyarwinthu/zeyocut/blob/main/SECURITY.md).

### Windows warnings

The exe isn't code-signed yet, so Windows may warn about it:

- **SmartScreen.** If you start the exe yourself, Microsoft Defender SmartScreen can show "Windows protected your PC". Once the checksum matches, choose **More info → Run anyway**. You don't normally start it yourself: the MCP client does.
- **Downloaded-file mark.** Windows marks files that come from the internet. After checking the checksum you can clear the mark before unzipping, with **right-click → Properties → Unblock** or `Unblock-File .\ZeyoCut-<version>-win-x64.zip`.
- **Smart App Control** (Windows 11) can block programs that aren't signed, and it can't be told to allow a single program.

## 2. Install FFmpeg

ZeyoCut uses FFmpeg to import video, draw video layers and export. Editing and previewing pictures work without it.

```powershell
winget install Gyan.FFmpeg
```

This puts `ffmpeg` and `ffprobe` on your `PATH`. Restart your MCP client afterwards so it sees the new `PATH`. To use an FFmpeg you unzipped yourself, set its path in the bundle's settings or with `FFMPEG_PATH` (the exe or its folder).

Transcripts are optional and need whisper.cpp; see [Transcripts](#transcripts-optional) below.

## 3. Connect a client

### Claude Desktop, with the bundle

1. Open **Settings → Extensions**. In newer versions this is **Settings → Desktop app**, then **Manage** next to Extensions.
2. Click **Advanced settings**, and under **Extension Developer** click **Install Extension…**. Choose `ZeyoCut-<version>.mcpb`.
3. Click ZeyoCut in the list to see its settings. All of them are optional:

| Setting | Leave empty for |
|---|---|
| Projects folder | `%USERPROFILE%\ZeyoCut\projects` |
| Import folder | your Desktop, Documents, Downloads, Music, Pictures and Videos (see [Folders](#folders)) |
| FFmpeg | FFmpeg on `PATH` |
| whisper-cli (optional) | whisper-cli on `PATH`, or no transcripts |
| Whisper model (optional) | a `ggml-*.bin` model next to whisper-cli |

### Claude Desktop, with the zip

1. Unzip the files to a folder of your own, for example `C:\Tools\ZeyoCut`.
2. In Claude Desktop open **Settings → Developer → Edit Config**. This opens `%APPDATA%\Claude\claude_desktop_config.json`. Add:

    ```json
    {
      "mcpServers": {
        "zeyocut": {
          "command": "C:\\Tools\\ZeyoCut\\ZeyoCut.Mcp.exe"
        }
      }
    }
    ```

    Settings go in an `"env"` object next to `"command"`, for example `"env": { "WHISPER_PATH": "C:\\Tools\\whisper" }`. In JSON every `\` is written `\\`.
3. Quit Claude Desktop fully (from the tray icon) and start it again.

If ZeyoCut doesn't show up, its log is `%APPDATA%\Claude\logs\mcp-server-zeyocut.log`.

### Claude Code

Unzip the files as above, then run:

```powershell
claude mcp add --transport stdio --scope user zeyocut -- C:\Tools\ZeyoCut\ZeyoCut.Mcp.exe
```

`--scope user` makes ZeyoCut available in every folder you use Claude Code in; leave it out to add it to the current folder only. Settings are added with `--env`, which must come before `--transport`:

```powershell
claude mcp add --env WHISPER_PATH=C:\Tools\whisper --transport stdio --scope user zeyocut -- C:\Tools\ZeyoCut\ZeyoCut.Mcp.exe
```

`claude mcp list` should then show zeyocut as connected. Inside Claude Code, `/mcp` shows the same.

### Other MCP clients

Any client that runs local (stdio) MCP servers can use ZeyoCut. The command is the full path to `ZeyoCut.Mcp.exe`, with no arguments; settings are environment variables. Many clients use the same `mcpServers` form as Claude Desktop above. VS Code uses `.vscode/mcp.json`:

```json
{
  "servers": {
    "zeyocut": {
      "type": "stdio",
      "command": "C:\\Tools\\ZeyoCut\\ZeyoCut.Mcp.exe"
    }
  }
}
```

Two things to know:

- Tools such as `render_frame` and `contact_sheet` return PNG images so the model can check its work. With a client that doesn't pass images to the model, everything still works, but the model edits without seeing the result.
- ZeyoCut has 90 tools. Some clients limit how many tools they turn on at once.

## 4. First run

Ask the model:

> Run ZeyoCut's system check.

`system_check` reports whether FFmpeg and whisper were found, with what to do if not, which folders ZeyoCut uses, and the CPU, RAM, GPU and AI models of this computer. Then try something small:

> Create a ZeyoCut project "hello", 1280x720, 3 seconds, with the title "Hello" popping in, and show me a frame at 1 second.

The [guide](GUIDE.md) shows every tool with an example call and its real response.

## Folders

| What | Where |
|---|---|
| Projects | `%USERPROFILE%\ZeyoCut\projects` |
| Library (rigs, actions, text styles, templates, sound effects) | `%USERPROFILE%\ZeyoCut\library` |
| AI models, downloaded on first use | `%USERPROFILE%\ZeyoCut\library\models` |
| Logs and the record of background jobs | `%LOCALAPPDATA%\ZeyoCut` |
| Native libraries, unpacked from the exe on first run | `%TEMP%\.net\ZeyoCut.Mcp` |

ZeyoCut only reads files from the projects folder, the library and the **import folders**: by default your Desktop, Documents, Downloads, Music, Pictures and Videos. Importing from anywhere else gives an error that names the setting. To add folders, set `ZEYOCUT_IMPORT_DIRS` (folders separated by `;`), the bundle's Import folder setting, or `ImportFolders` in `appsettings.json`. Projects must be inside the projects folder.

Every setting is listed under [Configuration](https://github.com/zeyarwinthu/zeyocut#configuration) in the README.

## Transcripts (optional)

`analyze_video` makes a transcript with a time for every word when whisper.cpp is installed. Cutting by words and automatic captions need it; without it, analysis still finds scene cuts and silences.

1. Download a Windows build of whisper.cpp, for example `whisper-bin-x64.zip` from its GitHub releases, and unzip it to `C:\Tools\whisper`.
2. Put a model in `C:\Tools\whisper\models`. `ggml-small.bin` (about 470 MB) is a good balance of speed and accuracy, and handles many languages.
3. Set `WHISPER_PATH` to `C:\Tools\whisper` (or choose `whisper-cli.exe` in the bundle's settings), and restart the client.

## Privacy

ZeyoCut has no telemetry and no account. Your projects and media stay on your computer. It goes online for one thing only: downloading an AI model from its original source the first time a feature needs it, checked against a pinned SHA-256. The preview page runs on `127.0.0.1`, reachable only from your own computer. To run models on a GPU, ZeyoCut uses Microsoft's DirectML, which may send diagnostic information to Microsoft as your Windows diagnostic data settings allow; `AiDevice` `cpu` keeps every model on the CPU. What you make with ZeyoCut is yours; see [Privacy](privacy.md) for the details.

## Update and uninstall

**Update.** With the bundle, install the new `.mcpb` the same way. With the zip, quit the client, replace `ZeyoCut.Mcp.exe` (keep your `appsettings.json` if you changed it) and start the client again. Projects saved by an older version open in a newer one.

**Uninstall.** Remove ZeyoCut from the client: on Claude Desktop's Extensions page, by deleting its entry from `claude_desktop_config.json`, or with `claude mcp remove zeyocut`. Then delete the folder you unzipped it to, `%LOCALAPPDATA%\ZeyoCut` and `%TEMP%\.net\ZeyoCut.Mcp`. Your projects and library are in `%USERPROFILE%\ZeyoCut`; delete that folder only if you don't want them any more.

## Build from source

Building needs the .NET 10 SDK; see [Build and test](https://github.com/zeyarwinthu/zeyocut#build-and-test) in the README and [CONTRIBUTING.md](https://github.com/zeyarwinthu/zeyocut/blob/main/CONTRIBUTING.md).
