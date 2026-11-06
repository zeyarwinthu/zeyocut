# Examples

Three small projects, made with ZeyoCut's own tools from art and sounds that ZeyoCut draws and generates itself. Each folder holds `project.json` and `assets\`; renders, exports and backups are made when you use them. For a video of your own, the [talking-head cleanup](#talking-head-cleanup) steps cut the pauses, add captions and export it.

| Project | What it shows |
|---|---|
| [`tora-jump`](tora-jump) | A cutout character rig: squash and stretch, easing, a drawing swap, a lagging head and a wagging tail, sound effects, and a Japanese subtitle |
| [`tora-and-mimi`](tora-and-mimi) | A rig and an action from the library: two copies of one character, each playing the same jump at its own time and speed, with a title in English, Japanese and Burmese versions |
| [`comic-panels`](comic-panels) | A comic-panel video: panels with zooms and a pan, a crossfade and a slide, sound effects, and caption tracks in three languages |

## Open one

Projects live in the projects folder, so copy the example there first:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\ZeyoCut\projects" | Out-Null
Copy-Item -Recurse examples\tora-jump "$env:USERPROFILE\ZeyoCut\projects\"
```

Then ask:

> Open the ZeyoCut project tora-jump and show me a contact sheet.

From there, change anything, or export it:

> Export it as an MP4.

## tora-jump

The demo that `ZeyoCut.Mcp.exe demo <folder>` builds: Tora, a cartoon cat in three parts (body, head and tail) under an invisible rig root, jumps over 3 seconds. A prompt that makes something like it:

> Create a ZeyoCut project "tora-jump", 1280x720, 3 seconds, sky blue. Make a cat from body.png, head.png, head_happy.png and tail.png: the head parented at the neck with a normal and a happy drawing, the tail at the back. Make it jump with squash before takeoff, stretch in the air and an easeOutBack landing; the head lags behind the body, the face turns happy in the air, and the tail wags. Add a boing at takeoff, a thud at landing, and the title "Tora Jumps!" over the subtitle "トラのジャンプ". Show me a contact sheet with debug on.

## tora-and-mimi

Tora saved as a rig, with the jump saved as an action, then placed twice in a new project. The second copy is tinted gray as Mimi. The title has a version per language: `render_frame`, `render_contact_sheet` and the exports take `language` to show one.

> Open tora-jump. Save Tora as a rig called tora, and the jump from 0.5 to 2.4 seconds as an action called jump. Then make a 4-second project "tora-and-mimi" with the same sky, clouds and ground. Put Tora on the left and a gray copy, Mimi, on the right. Tora jumps at 0.2 seconds and Mimi at 1.3 seconds, a little faster. Add the title "Hello!" in English, "こんにちは！" in Japanese and "မင်္ဂလာပါ" in Burmese, and show me each version at 1.6 seconds.

The tool calls that built it:

1. In tora-jump: `save_rig` (`rootLayerId` tora, `name` tora) and `save_action` (`rigRootId` tora, 0.5 to 2.4 s, `name` jump).
2. `create_project` (1280x720, 30 fps, 4 s, `#BFE6FF`), `import_asset` clouds.png and ground.png from tora-jump, and `add_image_layer` for each.
3. `add_rig` tora twice: `instanceName` tora at (300, 605) and mimi at (760, 605).
4. `set_effect` tint `#6F7C93` at 0.45 on `mimi_body`, `mimi_head` and `mimi_tail`.
5. `apply_action` jump on `tora_tora` at 0.2 s, and on `mimi_tora` at 1.3 s with `speed` 1.4.
6. `add_text_layer` three times, with `language` en, ja and my, then `check_project`: no problems.

## comic-panels

Three panels drawn from tora-jump (with its titles hidden, and a white border added to each picture), made into a 9-second video. Each panel has a camera move: a slow zoom in, a pan, and a zoom out. A crossfade and a slide join them. The captions are a track per language, so each version shows only its own.

> Make a 9-second comic video from panel1.png, panel2.png and panel3.png, 3 seconds each, on a dark background. Zoom slowly into the first, pan across the second and zoom out of the last. Crossfade into the second panel and slide into the third. Add a boing when Tora jumps and a thud when he lands. Add captions in English, Japanese and Burmese, each as its own track:
> 1. "Tora sits in the sun." / "トラはひなたぼっこ。" / "တိုရာ နေပူထဲမှာ ထိုင်နေတယ်။"
> 2. "Then he jumps as high as he can!" / "そして、思いきりジャンプ！" / "ပြီးတော့ အမြင့်ကြီး ခုန်လိုက်တယ်။"
> 3. "And lands with a big smile." / "にっこり着地。" / "ပြုံးပြုံးလေးနဲ့ ပြန်ဆင်းလာတယ်။"

The tool calls that built it:

1. `create_project` (1280x720, 30 fps, 9 s, `#1E1E1E`) and `import_folder` with `pattern` panel\*.png.
2. `add_image_layer` for each panel at 0, 3 and 6 s, scaled 0.86.
3. `set_keyframes`: `scaleX`/`scaleY` 0.86 → 0.94 on panel1, `x` 600 → 680 on panel2, 0.94 → 0.86 on panel3.
4. `add_transition` crossfade panel1 → panel2 and slide panel2 → panel3, 0.5 s each.
5. `import_asset` boing.wav and thud.wav, and `add_audio_layer` at 3.4 s and 6.3 s with `role` effects.
6. `add_caption_track` for en, ja and my, then `check_project`: no problems.

To export every language for YouTube and Shorts in one job:

> Export the comic in every language for YouTube and as a Short.

## Talking-head cleanup

This one has no project folder: it needs a video of someone talking, and the repository keeps video files out. Use a clip of your own. Put it in your Videos folder (or another [import folder](../README.md#configuration)) and pick ZeyoCut's `talking_head_cleanup` prompt with the video's path. In Claude Code, that's a slash command:

```text
/mcp__zeyocut__talking_head_cleanup C:\Users\you\Videos\talk.mp4
```

Other clients that support MCP prompts show it in their own way, often in a menu. Or ask in your own words:

> Clean up Videos\talk.mp4: cut the pauses with punch-in zooms, add captions, check the loudness, and export it for YouTube and as a Short.

The prompt takes the model through these steps:

1. `create_project` with the `youtube_1080p` preset, then `import_video` and `add_video_layer`.
2. `analyze_video` finds the silences and, with [whisper](../docs/install.md#transcripts-optional), transcribes the words. Without whisper there are no captions.
3. `remove_silences` as a dry run. **The model stops here** and tells you how many pauses it would cut and how much shorter the video gets; go ahead, or keep more of the pauses.
4. `remove_silences` with `punchIn`: every second piece is zoomed in a little, which hides the jump cuts. Filler words can go too, with `cut_by_transcript`.
5. `generate_captions`, then `render_contact_sheet` to look at the cuts, the punch-ins and the captions.
6. `audio_levels` checks the loudness (exports are normalized to -14 LUFS), and `check_project` and `frame_diff` look for mistakes.
7. `export_batch` with `youtube_1080p` and `shorts_1080p`: a 16:9 video, and a 9:16 one with the whole picture over a blurred copy of itself.

For a Short cropped to the speaker instead, ask for one: `reframe_to_vertical` keeps the speaker in the frame. The [guide](../docs/GUIDE.md#workflow-prompts) shows the whole prompt. In the test suite, these steps run on a generated 15.9-second talk: they cut 3 pauses (5.6 s), added 3 captions, measured -14.6 LUFS and exported both shapes.
