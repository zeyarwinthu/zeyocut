// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ZeyoCut.Mcp;

// Step-by-step recipes that name the tools to call and where to stop and ask the user.
[McpServerPromptType]
public static class WorkflowPrompts
{
    [McpServerPrompt(Name = "talking_head_cleanup")]
    [Description("Clean up a talking-head video: cut the pauses with punch-in zooms, add captions, check the loudness and export 16:9 and 9:16.")]
    public static string TalkingHeadCleanup(
        [Description("Absolute path of the video.")] string video,
        [Description("Project name. Default: the video's file name.")] string? name = null,
        [Description("Spoken language code for the transcript (en, ja, my, ...). Default auto.")] string? language = null)
    {
        var path = Required(video, "video", "the absolute path of the video");
        var project = Name(name, Path.GetFileNameWithoutExtension(path));
        var spoken = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();
        return $$"""
            Clean up the talking-head video {{path}} with the ZeyoCut tools, step by step. Report briefly after each step. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

            1. create_project with name "{{project}}", preset youtube_1080p and duration 1; the clip extends the project to its own length.
            2. import_video with path "{{path}}" (footage that needs normalizing is done in the background), then add_video_layer with the asset id it returned.
            3. analyze_video on that asset with language "{{spoken}}". It finds the silences and transcribes the words. Without a transcript (whisper isn't set up), tell the user that captions can't be made, and leave out step 6.
            4. remove_silences on the clip with dryRun=true. Stop and ask the user: tell them how many pauses it would cut and how much shorter the video gets, and ask whether to go ahead or keep more of the pauses (a higher minSilence).
            5. When they agree, remove_silences again with dryRun=false and punchIn=true. Every second piece is zoomed in (punchInZoom, default 1.1), which hides the jump cuts. If get_transcript shows filler words or false starts, offer to cut them with cut_by_transcript.
            6. generate_captions with the asset id, in style subtitle unless the user asked for another. The captions follow the cuts; after more cuts, run it again.
            7. render_contact_sheet over the whole video, to look at the cuts, the punch-ins and the captions.
            8. Loudness: exports are normalized to -14 LUFS (update_project loudnessTarget changes it). audio_levels measures the mix as it will be exported; fix clipping or sections it flags as much quieter.
            9. check_project and frame_diff. Fix what they find.
            10. export_batch with presets ["youtube_1080p", "shorts_1080p"]: a 16:9 file, and a 9:16 one with the whole picture over a blurred copy of itself. When the job is done, give the user both file paths.

            For a 9:16 version cropped to the speaker instead: update_project preset=shorts_1080p, reframe_to_vertical with focusX on the speaker, generate_captions again, then start_export preset=shorts_1080p. Undo, three times, goes back to the 16:9 project.
            """;
    }

    [McpServerPrompt(Name = "privacy_blur")]
    [Description("Find the faces and license plates in a video, let the user choose which to hide, hide them and check every frame before export.")]
    public static string PrivacyBlur(
        [Description("Absolute path of the video.")] string video,
        [Description("What to find: faces, plates or both. Default both.")] string? target = null,
        [Description("Project name. Default: the video's file name.")] string? name = null)
    {
        var path = Required(video, "video", "the absolute path of the video");
        var project = Name(name, Path.GetFileNameWithoutExtension(path));
        var what = string.IsNullOrWhiteSpace(target) ? "both" : target.Trim().ToLowerInvariant();
        var find = what switch
        {
            "faces" => ("faces", "detect_and_track on the clip with target faces. It runs as a detect_N job"),
            "plates" => ("license plates", "detect_and_track on the clip with target plates. It runs as a detect_N job"),
            "both" => ("faces and license plates", "detect_and_track on the clip with target faces, then again with target plates. Each runs as a detect_N job"),
            _ => throw new McpException($"unknown target '{target}'; valid values: faces, plates, both"),
        };
        return $$"""
            Hide the {{find.Item1}} in the video {{path}} with the ZeyoCut tools, step by step. Nothing is hidden until the user has chosen what to hide. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

            1. create_project with name "{{project}}", preset youtube_1080p and duration 1; the clip extends the project to its own length.
            2. import_video with path "{{path}}" (footage that needs normalizing is done in the background). If the clip is taller than wide, update_project preset=shorts_1080p. Then add_video_layer with the asset id it returned.
            3. {{find.Item2}}. Also run analyze_video on the asset: a redaction's box isn't held across the scene cuts it finds. If a reply says the job would take more than 5 minutes, tell the user the estimate, and start it with confirmLongJob=true only if they agree.
            4. Stop and ask the user. Show them the contact sheets from get_job_status, which show every track with its id, and ask which to hide: some ids, or all. Tracks with a low confidence may be something else. If something they want hidden wasn't found, draw a box around it at one time and follow it with track_object.
            5. apply_redaction with the tracks they chose, in style pixelate unless they want blur or solid.
            6. redaction_report. Look at each span it lists with render_frame debug=true (it draws each track's box) at the span's start, middle and end. The report can't see the picture, so a span where nothing shows is fine. Fix what does show: raise paddingPercent or holdFrames on the redaction layer with update_layer, redraw a box that drifted with reanchor_track (tracks from track_object), or correct a box by hand after tracks_to_keyframes. A box held past a cut that analyze_video didn't find covers part of the next shot for holdFrames frames; if that shows, lower holdFrames. After a fix, run redaction_report again. Tell the user exactly what still shows and where, if anything does.
            7. Stop and ask the user before exporting: show them the report's summary and a render_contact_sheet. When they agree, start_export with the project's preset.
            """;
    }

    [McpServerPrompt(Name = "new_episode")]
    [Description("Start a new animated episode from the library: a template, the scenes as markers, then the rigs, actions, sounds and titles of each scene.")]
    public static string NewEpisode(
        [Description("Project name for the episode.")] string name,
        [Description("Library template to start from. Default: ask the user.")] string? template = null,
        [Description("Absolute path of an episode script (.json or .md) for build_from_script. Optional.")] string? script = null)
    {
        var project = Required(name, "name", "the episode's project name");
        var start = string.IsNullOrWhiteSpace(script)
            ? $"""
                2. If the user has a script (scenes with rigs, actions, sounds and captions at times), build_from_script builds the whole episode in one call (docs/GUIDE.md has the JSON and Markdown formats): use it with name "{project}", tell the user what it couldn't map, and go on at step 6. Otherwise:
                3. {Template(project, template)}
                """
            : $"""
                2. build_from_script with scriptFile "{script.Trim().Trim('"')}" and name "{project}" builds the whole episode: the scenes with a scene marker each, rigs, actions, sound effects and captions. Tell the user what it couldn't map, add those by hand as in step 5, and go on at step 6.
                3. (Without a script: {char.ToLowerInvariant(Template(project, template)[0])}{Template(project, template)[1..]})
                """;
        return $$"""
            Start the new episode "{{project}}" from the ZeyoCut library, step by step. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

            1. list_library shows the templates, rigs, actions and styles; list_sfx shows the sound effects.
            {{start}}
            4. Stop and ask the user for the scenes if they haven't given them: a name and a length for each. add_markers with kind scene at each scene's start.
            5. For each scene:
               - add_rig for each character in it, with startTime at the scene's start, x and y where the character stands, and an instanceName (a second copy of a rig needs its own). To take a character off at the scene's end, update_layer end on its root layer; its parts follow.
               - apply_action for what each character does (startTime, speed, loops), add_sfx for the sounds, and add_text_layer with a library style for titles or dialogue.
            6. render_contact_sheet over each scene (startTime and endTime from the markers) to check the poses, timing and framing. Use render_frame debug=true when a rig looks wrong.
            7. check_project, frame_diff and audio_levels; fix what they find. Then start_export with the project's preset.
            """;
    }

    [McpServerPrompt(Name = "comic_panels")]
    [Description("Turn a folder of comic panels into a video: the camera moves from panel to panel, timed to a voiceover, with captions.")]
    public static string ComicPanels(
        [Description("Absolute path of the folder with the panels.")] string folder,
        [Description("Absolute path of the voiceover. Optional.")] string? voiceover = null,
        [Description("Project name. Default: the folder's name.")] string? name = null)
    {
        var path = Required(folder, "folder", "the absolute path of the folder with the panels").TrimEnd('\\', '/');
        var project = Name(name, Path.GetFileName(path));
        var voice = string.IsNullOrWhiteSpace(voiceover) ? null : voiceover.Trim().Trim('"');
        var timing = voice is null
            ? """
                3. There is no voiceover: stop and ask the user how long each panel should stay.
                4. The panels follow each other with those lengths; update_project duration to their total.
                """
            : $"""
                3. The voiceover: import_asset with path "{voice}" unless import_folder brought it in, add_audio_layer with role voice, and update_project duration to its length plus one second. analyze_video on its asset transcribes the words with their times; get_transcript reads them.
                4. Time the panels: split the narration at sentence ends into one part per panel, in order. Stop and ask the user to confirm the plan: each panel with its time and the words over it.
                """;
        var captions = voice is null
            ? "8. If the user wants captions, add them with add_text_layer (a library style suits them), then update_layer with changes {\"ignoreCamera\": true}, so the camera doesn't move them."
            : "8. generate_captions with the voiceover's asset id. Caption groups ignore the camera.";
        return $$"""
            Turn the comic panels in {{path}} into a video with the ZeyoCut tools, step by step: the camera moves from panel to panel{{(voice is null ? "" : $", timed to the voiceover {voice}, with captions")}}. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

            1. create_project with name "{{project}}" and preset youtube_1080p, or shorts_1080p if the panels are taller than wide.
            2. import_folder with path "{{path}}". The files come in name order, numbers by value (panel_2 before panel_10); if that isn't the reading order, ask the user.
            {{timing}}
            5. Lay the panels out in a row: add_image_layer for each, at scale 1, with y at the canvas' vertical center. The first panel's x is the canvas' horizontal center; each next panel's x adds half the previous panel's width, a gap of half the canvas width, and half its own width, so the neighbours stay out of the frame.
            6. Move the camera with one set_keyframes call on layerId "camera", properties x, y and zoom:
               - at each panel's start, x and y at the panel's center and zoom 0.92 times the smaller of canvas width / panel width and canvas height / panel height, so the whole panel fits;
               - the same values again 0.5 s before the next panel's start, with easing easeInOut, so the camera holds and then pans; a zoom 5% higher there gives a slow push-in.
            7. add_markers with kind scene at each panel's start, named after the panel.
            {{captions}}
            9. render_contact_sheet over the whole video: each panel should fill the frame in its time, with the captions readable. check_project and audio_levels; fix what they find. Then start_export with the project's preset.
            """;
    }

    private static string Required(string? value, string argument, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new McpException($"give {argument}: {what}") : value.Trim().Trim('"');

    private static string Name(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static string Template(string project, string? template) =>
        string.IsNullOrWhiteSpace(template)
            ? $"Stop and ask the user which template to start from (list_library shows them), then new_project_from_template with it, name \"{project}\" and the episode's length as duration; or create_project with a preset for a blank episode."
            : $"new_project_from_template with template \"{template.Trim()}\", name \"{project}\" and the episode's length as duration.";
}
