# Changelog

All notable changes to ZeyoCut are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and ZeyoCut follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, a minor version (0.x.0)
can change tools, settings or the project format; the project format always migrates older projects
when they open.

## [Unreleased]

## [0.1.0] - 2026-11-06

The first public release: a headless 2D animation and video editor for Windows whose only interface
is an MCP server, for Claude and other MCP clients.

### Added

- **Projects:** create, open and template projects; undo and redo; a backup before every save; a
  project format with migrations from every earlier version.
- **Cutout animation:** image, text and video layers in a parent–child hierarchy; keyframes with
  easing (overshoot, bounce, elastic); drawing swaps; motion paths; camera moves and shake; effects
  (shadow, outline, blur, tint, color) and masks, all animatable.
- **Library:** rigs, actions (walk cycles, jumps), text styles, project templates and sound effects
  shared by all projects; episode scripts in Markdown or JSON that build a project in one call.
- **Animating to sound:** beat detection and automatic lip flap from a voice.
- **Footage editing:** import with normalization (variable frame rate, rotation, HDR) and proxies for
  large footage; analysis of scene cuts, silences and a word-level transcript (whisper.cpp); ripple
  cuts of silences and filler words; split, trim, move, speed; transitions.
- **Captions and languages:** caption tracks per language from text, SRT or the transcript; Burmese and
  Japanese shaped with HarfBuzz; a font per language; a check for characters a font can't show; SRT
  export; one call that exports every language.
- **Background removal:** chroma key, edge fill, and AI models for images and video (downloaded on
  first use); text behind the subject.
- **Tracking and privacy:** object tracking with corrections; attaching layers to a track; AI face and
  license-plate detection with tracking; redaction (pixelate, blur, solid) with a fail-safe that holds
  boxes across gaps and stops at scene cuts; a redaction report of the spans to check.
- **Checks:** frame-diff glitch detection, audio levels of the exported mix, and a project check
  (missing files, text under the Shorts player UI, keyframes that never play).
- **Export:** H.264 MP4 with music ducked under voice and loudness normalized (-14 LUFS by default);
  YouTube and Shorts presets and both from one call; GPU encoders where they work; thumbnails, image
  sequences, chapter lists; OpenTimelineIO and FCPXML for other editors, with prerendered layers.
- **Preview:** rendered frames and contact sheets that match the export exactly, and a local preview
  page that plays the edit with sound.
- **System check:** whether FFmpeg and whisper are installed, with how to install them; the folders
  ZeyoCut uses; the CPU, RAM, GPU and AI models, with a benchmark that picks the CPU or DirectML per
  model.
- **Workflow prompts** for talking-head cleanup, privacy blur, new episodes and comic panels.
- **Install:** an MCP Bundle (`.mcpb`) for Claude Desktop and a zip with the single exe for any MCP
  client, with their SHA-256 checksums.
- **Docs:** an install guide, a guide to every tool, a tool reference generated from the server, the
  project format, the architecture, three example projects, and the website https://cut.zeyoapp.com.

### Security

- Tools read files only from the projects folder, the library and the import folders (the
  `ImportFolders` setting), and projects live in the projects folder.
- A project, template or rig can't point an asset outside its own folder.
- ONNX Runtime's Windows telemetry is turned off; ZeyoCut itself collects nothing.

[Unreleased]: https://github.com/zeyarwinthu/zeyocut/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/zeyarwinthu/zeyocut/releases/tag/v0.1.0
