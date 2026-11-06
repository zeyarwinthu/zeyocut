// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class CaptionTools
{
    // behindSubject=auto measures up to this many caption frames directly; more, or a matte still
    // being computed, and the fades come from a background job.
    public const int AutoDirectFrames = 600;

    [McpServerTool(Name = "generate_captions")]
    [Description("Turn the transcript of an analyzed asset (analyze_video) into caption text layers under a null layer 'captions' (one undo step). Word times are mapped through the clips that play the asset, so silences and words cut before this are followed; run it again after later cuts and it replaces the old captions. A cue holds up to maxLines balanced lines of maxWordsPerLine words and ends at a sentence end or a pause. Styles: subtitle (white on a dark box, bottom), bold_center (large bold white with a black outline, center; lower-middle on vertical canvases), minimal (small white with a thin outline, bottom), or the name of a library style (save_style), scaled to the canvas and placed at its captionPosition. Move or hide all captions through the 'captions' layer. behindSubject=on puts the captions behind the person in the clip, as text_behind_subject does: a cutout of their own ('captions_subject', aiVideo or the matte of the clip's text_behind_subject cutout) draws the subject again above them; a clip whose background is removed already just goes in front of them. behindSubject=auto does that too, but wherever the subject would cover more than occlusionThreshold percent of a caption, the captions show in front: the cutout fades out over 0.25 s before such a span and back in after (spans less than 0.5 s apart join); with a clip that is its own subject, those captions draw in front of it. Auto measures every caption frame; up to 600 frames with the matte computed answers directly, otherwise the captions are added at once and the fades follow from a background job (captions_N) as their own undo step.")]
    public static string GenerateCaptions(
        EditorState state,
        [Description("Asset whose clips get captions (all video/audio layers playing it). Give this or layerId.")] string? assetId = null,
        [Description("Only this video/audio layer. Give this or assetId.")] string? layerId = null,
        [Description("subtitle, bold_center, minimal or a library style name. Default subtitle.")] string style = "subtitle",
        [Description("Words per line, 1-20. Default 6.")] int maxWordsPerLine = 6,
        [Description("Lines per cue, 1-4. Default 2.")] int maxLines = 2,
        [Description("Font family. Default Segoe UI; use \"Myanmar Text\" for Burmese, \"Yu Gothic\" for Japanese.")] string? fontFamily = null,
        [Description("Font size in pixels. Default: scaled to the canvas per style.")] double? fontSize = null,
        [Description("Text color. Default #FFFFFF.")] string? color = null,
        [Description("Id of the caption group (null layer). Default captions.")] string groupId = CaptionBuilder.DefaultGroup,
        [Description("off, on or auto: put the captions behind the subject of the clip (on), and in front where it would cover too much of a caption (auto). Default off; off also takes away the cutout an earlier on/auto made.")] string behindSubject = "off",
        [Description("behindSubject=auto: how much of a caption, in percent, the subject may cover before the captions show in front. Default 35.")] double? occlusionThreshold = null,
        [Description("behindSubject: start the subject's AI matte even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first.")] bool confirmLongJob = false) => Guard(() =>
    {
        var mode = EnumNames.Parse<BehindSubject>(behindSubject, "behindSubject");
        var threshold = occlusionThreshold ?? CaptionBuilder.DefaultOcclusionThreshold;
        if (!double.IsFinite(threshold) || threshold is <= 0 or >= 100)
        {
            throw new ZeyoCutException($"occlusionThreshold must be between 0 and 100 percent (got {Num(threshold)})");
        }

        var session = state.Session;
        var assetForTranscript = session.Read(p => CaptionBuilder.Sources(p, assetId, layerId).Asset);
        var transcript = AnalysisFiles.RequireTranscript(session.Directory, assetForTranscript);
        var libraryStyle = CaptionStyle(state, style);

        var options = new CaptionOptions
        {
            AssetId = assetId,
            LayerId = layerId,
            Style = style,
            MaxWordsPerLine = maxWordsPerLine,
            MaxLines = maxLines,
            FontFamily = fontFamily,
            FontSize = fontSize,
            Color = color,
            GroupId = groupId,
            LibraryStyle = libraryStyle,
            BehindSubject = mode,
        };
        var renderer = state.Renderer;
        CaptionResult Build(Project p)
        {
            var styled = libraryStyle is null ? options : options with { FontAsset = TextStyles.AdoptFont(p, session.Directory, state.Library, libraryStyle) };
            return CaptionBuilder.Generate(p, transcript, styled, new TextMeasurer(renderer, p));
        }

        var label = $"generate_captions {assetId ?? layerId}";
        var unused = occlusionThreshold is not null && mode != BehindSubject.Auto ? "occlusionThreshold is only used with behindSubject=auto" : null;
        if (mode == BehindSubject.Off)
        {
            var off = session.Mutate(label, Build);
            return CaptionBuilder.Describe(off) + (unused is null ? "" : "\n" + unused);
        }

        // Planned on a copy first: which mattes the subject needs and how many frames auto measures.
        var directory = session.Directory;
        var plan = session.Snapshot();
        var before = ProjectJson.Serialize(plan);
        var planned = Build(plan);
        var subject = planned.Subject!;
        var mattes = CoverMattes(plan, directory, subject, all: mode == BehindSubject.Auto);
        var ffmpeg = mattes.Any(m => m.Kind == MatteMethod.AiVideo) ? state.RequireFfmpeg() : null;
        var computed = mattes.All(m => m.Targets.All(t => File.Exists(t.Path)));
        var frames = Occlusion.TextFrames(plan, planned.LayerIds).Sum(s => s.Last - s.First + 1);
        var estimates = mattes.Select(m => MatteTools.Estimate(state, m.Kind, m.Targets)).OfType<SystemTools.JobEstimate>().ToList();
        if (SystemTools.NotStarted(estimates, confirmLongJob, "generate_captions") is { } notStarted)
        {
            return notStarted;
        }
        if (mode == BehindSubject.Auto && computed && frames <= AutoDirectFrames)
        {
            var measured = Measure(state, directory, plan, planned, null, CancellationToken.None);
            var (done, auto) = session.Mutate(label, p =>
            {
                if (ProjectJson.Serialize(p) != before)
                {
                    throw new ZeyoCutException("the project changed while the captions were measured; run generate_captions again");
                }

                var made = Build(p);
                return (made, CaptionBuilder.Auto(p, made.Subject!, measured, threshold));
            });
            var direct = mattes.SelectMany(m => m.Targets).Select(t => $"{t.Label}: the matte is already computed: {MatteFiles.Relative(directory, t.Path)}").ToList();
            return Reply(done, direct.Concat(auto), unused);
        }

        var result = session.Mutate(label, Build);
        var after = session.Snapshot();
        var lines = new List<string>();
        var waits = new List<BackgroundJob>();
        foreach (var matte in CoverMattes(after, directory, result.Subject!, all: mode == BehindSubject.Auto))
        {
            var cutout = matte.Owner is CutoutLayer;
            lines.AddRange(MatteTools.StartAiMatte(state, matte.Owner.Id, cutout, matte.Kind, matte.Targets, ffmpeg, directory, cutout ? "generate_captions" : "remove_background"));
            waits.AddRange(matte.Targets.Select(t => state.MatteJobs.TryGetValue(t.Path, out var job) ? job : null).OfType<BackgroundJob>());
        }

        if (mode == BehindSubject.Auto)
        {
            var job = StartFades(state, result, ProjectJson.Serialize(after), waits.Distinct().ToList(), threshold, frames);
            var wait = waits.Count > 0 ? $" once {string.Join(", ", waits.Select(w => w.Id).Distinct())} {(waits.Distinct().Count() == 1 ? "is" : "are")} done" : "";
            lines.Add($"auto: {frames} caption frames are measured in the background as {job.Id}{wait}, and the fades are added as their own undo step (an edit made meanwhile makes it refuse, so run generate_captions again then); until then the captions are behind the subject the whole time; poll get_job_status with jobId \"{job.Id}\"");
        }

        return Reply(result, lines, unused);
    });

    // The AI mattes of what draws over the captions: the captions' cutout (all: also the clips that
    // are their own subject, which auto has to wait for).
    private static List<(IMattable Owner, MatteMethod Kind, List<MatteTools.MatteTarget> Targets)> CoverMattes(Project project, string directory, CaptionSubject subject, bool all)
    {
        var mattes = new List<(IMattable Owner, MatteMethod Kind, List<MatteTools.MatteTarget> Targets)>();
        foreach (var id in subject.Cover)
        {
            if (project.FindLayer(id) is IMattable { Matte: { } matte } owner && MatteFiles.IsAi(matte.Method) && (all || owner is CutoutLayer))
            {
                var source = owner is CutoutLayer cutout ? (IMattable)ProjectEditor.GetLayer(project, cutout.Sources[0]) : owner;
                mattes.Add((owner, matte.Method, MatteTools.AiTargets(project, directory, owner, source, matte)));
            }
        }

        return mattes;
    }

    private static List<CaptionCover> Measure(EditorState state, string directory, Project project, CaptionResult result, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var renderer = new FrameRenderer(directory, () => state.RequireFfmpeg().Ffmpeg, streamVideo: true);
        return Occlusion.Captions(renderer, project, result.LayerIds, result.Subject!.Cover, progress, cancellationToken);
    }

    // behindSubject=auto in the background: waits for the mattes, measures the captions and adds the
    // fades as one undo step, refused if the project changed since the captions were added.
    private static BackgroundJob StartFades(EditorState state, CaptionResult result, string added, List<BackgroundJob> waits, double threshold, int frames)
    {
        var session = state.Session;
        var directory = session.Directory;
        var subject = result.Subject!;
        var target = subject.Change.Cutout?.Id is { } id ? $"'{id}'" : "the captions";
        return state.Jobs.Start("captions", $"generate_captions behindSubject=auto: fades for {target} ({frames} caption frames)", (job, ct) =>
        {
            foreach (var wait in waits)
            {
                while (!wait.Completion.Wait(500, ct))
                {
                    job.Report(0, $"waiting for {wait.Id} ({wait.Percent}%)");
                }
            }

            var project = session.Snapshot();
            var pending = MatteFiles.Pending(project, directory).Where(p => subject.Cover.Contains(p.LayerId)).ToList();
            if (pending.Count > 0)
            {
                var failed = waits.Where(w => w.State == JobState.Failed).Select(w => $"{w.Id} failed: {w.Error}").ToList();
                throw new ZeyoCutException(
                    $"the matte isn't computed ({string.Join(", ", pending.Select(MatteFiles.Describe))}{(failed.Count > 0 ? "; " + string.Join("; ", failed) : "")}), so the fades weren't added; run generate_captions again");
            }

            if (ProjectJson.Serialize(project) != added)
            {
                throw new ZeyoCutException("the project changed after the captions were added, so the fades weren't added (the captions stay behind the subject the whole time); run generate_captions again");
            }

            var measured = Measure(state, directory, project, result, p => job.Report(p, "measuring the captions"), ct);
            var lines = session.Mutate("generate_captions auto fades", p =>
            {
                if (ProjectJson.Serialize(p) != added)
                {
                    throw new ZeyoCutException("the project changed while the captions were measured, so the fades weren't added (the captions stay behind the subject the whole time); run generate_captions again");
                }

                return CaptionBuilder.Auto(p, subject, measured, threshold);
            });
            return lines[0] + string.Concat(lines.Skip(1).Select(l => "\n- " + l));
        });
    }

    private static string Reply(CaptionResult result, IEnumerable<string> lines, string? unused)
    {
        var all = lines.ToList();
        if (result.Subject?.Change.Cutout is { } cutout)
        {
            all.Add($"check it with render_frame (layerId=\"{cutout.Id}\" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of a caption it covers");
        }

        if (unused is not null)
        {
            all.Add(unused);
        }

        return CaptionBuilder.Describe(result) + string.Concat(all.Select(l => "\n- " + l));
    }

    [McpServerTool(Name = "add_caption_track")]
    [Description("Add the captions of one language as a track of their own: text layers under a null layer captions_<language> (e.g. captions_ja) tagged with that language, so each language's version shows only its own captions (render_frame and start_export take language). Give exactly one source: cues (the start, end and text of each caption, e.g. a translation you wrote from get_transcript), srt (SRT subtitle text, or the path to a .srt file) or fromTranscript (an analyzed asset: its transcript's words, timed through the clips that play it, as generate_captions does). Adding a language's track again replaces it; one undo step. The captions get the language's font from the settings (my: Myanmar Text, ja: Yu Gothic) unless fontFamily is given, and the look of the style: subtitle (default), bold_center, minimal or a library style. Cues without text or past the project end are left out, one running past the end is shortened, and overlapping cues are named. The reply warns about characters the font has no glyphs for.")]
    public static string AddCaptionTrack(
        EditorState state,
        [Description("The captions' language: en, ja, my, pt-BR.")] string language,
        [Description("The captions: [{start, end, text}], seconds on the timeline; \\n in a text breaks the line.")] CueArg[]? cues = null,
        [Description("SRT subtitle text, or the path to a .srt file.")] string? srt = null,
        [Description("An analyzed asset (analyze_video) whose transcript becomes the captions.")] string? fromTranscript = null,
        [Description("subtitle, bold_center, minimal or a library style name. Default subtitle.")] string style = "subtitle",
        [Description("Font family. Default: the language's font from the settings, otherwise the style's.")] string? fontFamily = null,
        [Description("Font size in pixels. Default: scaled to the canvas per style.")] double? fontSize = null,
        [Description("Text color. Default #FFFFFF.")] string? color = null) => Guard(() =>
    {
        var tag = Languages.Normalize(language);
        var given = (cues is null ? 0 : 1) + (string.IsNullOrWhiteSpace(srt) ? 0 : 1) + (string.IsNullOrWhiteSpace(fromTranscript) ? 0 : 1);
        if (given != 1)
        {
            throw new ZeyoCutException(given == 0 ? "give the captions as cues, srt or fromTranscript" : "give only one of cues, srt and fromTranscript");
        }

        if (cues is { Length: 0 })
        {
            throw new ZeyoCutException("cues is empty; give at least one {start, end, text}");
        }

        var session = state.Session;
        var libraryStyle = CaptionStyle(state, style);
        var font = fontFamily ?? Languages.FontFor(state.Settings.Fonts, tag);
        var notes = new List<string>();
        List<CueEntry>? entries = null;
        Transcript? transcript = null;
        if (cues is not null)
        {
            entries = cues.Select(c => new CueEntry(c.Start, c.End, c.Text ?? "")).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(srt))
        {
            var (parsed, problems) = CaptionTracks.ParseSrt(SrtText(state.Settings, srt));
            entries = parsed;
            notes.AddRange(problems);
        }
        else
        {
            var asset = session.Read(p => CaptionBuilder.Sources(p, fromTranscript, null).Asset);
            transcript = AnalysisFiles.RequireTranscript(session.Directory, asset);
            if (Languages.TryNormalize(transcript.Language, out var spoken) && spoken.Split('-')[0] != tag.Split('-')[0])
            {
                notes.Add($"the transcript of '{asset.Id}' is in {spoken}, but these captions are tagged {tag}");
            }
        }

        var renderer = state.Renderer;
        var (track, used, warnings) = session.Mutate($"add_caption_track {tag}", p =>
        {
            List<CaptionCue> list;
            List<string> problems;
            if (transcript is null)
            {
                (list, problems) = CaptionTracks.Check(entries!, p.Duration);
            }
            else
            {
                var words = CaptionBuilder.TimelineWords(p, transcript, CaptionBuilder.Sources(p, fromTranscript, null).Layers);
                if (words.Count == 0)
                {
                    throw new ZeyoCutException("none of the transcript's words are on the timeline (the clips play other parts of the source)");
                }

                (list, problems) = (CaptionBuilder.Cues(words, 6, 2, p.Duration), []);
            }

            var options = new CaptionOptions
            {
                Style = style,
                FontFamily = font,
                FontSize = fontSize,
                Color = color,
                LibraryStyle = libraryStyle,
                FontAsset = font is null && libraryStyle is not null ? TextStyles.AdoptFont(p, session.Directory, state.Library, libraryStyle) : null,
            };
            var made = CaptionTracks.Add(p, tag, list, options, [.. notes, .. problems], new TextMeasurer(renderer, p));
            var texts = made.LayerIds.Select(id => (TextLayer)p.FindLayer(id)!).ToList();
            var layout = texts.SelectMany(t => renderer.Text.Layout(p, session.Directory, t).Warnings).Distinct().ToList();
            return (made, texts[0].FontAsset is { } fontAsset ? $"asset '{fontAsset}'" : texts[0].FontFamily, layout);
        });
        return WithWarnings(CaptionTracks.Describe(track, used), track.Problems.Concat(warnings));
    });

    [McpServerTool(Name = "import_srt")]
    [Description("Import a .srt subtitle file as the caption track of its language, as add_caption_track does with srt: text layers under a null layer captions_<language>, replacing that language's track if there is one (one undo step). The language is language, or else the one at the end of the file name (talk.ja.srt, talk_pt-BR.srt). The cue times are timeline times.")]
    public static string ImportSrt(
        EditorState state,
        [Description("Path to the .srt file.")] string path,
        [Description("The captions' language: en, ja, my, pt-BR. Default: the one at the end of the file name (talk.ja.srt).")] string? language = null,
        [Description("subtitle, bold_center, minimal or a library style name. Default subtitle.")] string style = "subtitle",
        [Description("Font family. Default: the language's font from the settings, otherwise the style's.")] string? fontFamily = null,
        [Description("Font size in pixels. Default: scaled to the canvas per style.")] double? fontSize = null,
        [Description("Text color. Default #FFFFFF.")] string? color = null) => Guard(() =>
    {
        var file = AllowedFolders.Readable(state.Settings, path ?? "");
        if (!File.Exists(file))
        {
            throw new ZeyoCutException($"srt file not found: {file}");
        }

        if (!Path.GetExtension(file).Equals(".srt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ZeyoCutException($"import_srt reads .srt subtitle files (got '{Path.GetFileName(file)}')");
        }

        var note = "";
        string tag;
        if (!string.IsNullOrWhiteSpace(language))
        {
            tag = Languages.Normalize(language);
        }
        else if (Languages.FromFileName(file) is { } named)
        {
            tag = named;
            note = $" (language {named} from the file name; give language to change it)";
        }
        else
        {
            throw new ZeyoCutException($"which language are the captions in? '{Path.GetFileName(file)}' doesn't end with one (as talk.ja.srt does); give language, e.g. en, ja, my or pt-BR");
        }

        var text = AddCaptionTrack(state, tag, srt: file, style: style, fontFamily: fontFamily, fontSize: fontSize, color: color);
        return $"imported {Path.GetFileName(file)}{note}\n{text}";
    });

    // srt: SRT text itself, or the path to a file of it.
    private static string SrtText(Settings settings, string srt)
    {
        if (srt.Contains("-->", StringComparison.Ordinal))
        {
            return srt;
        }

        var path = AllowedFolders.Readable(settings, srt);
        if (!File.Exists(path))
        {
            throw new ZeyoCutException($"srt file not found: {path}; srt takes SRT text or the path to a .srt file");
        }

        return File.ReadAllText(path);
    }

    // A built-in caption style (null) or the library style of that name.
    private static TextStyle? CaptionStyle(EditorState state, string style)
    {
        if (CaptionBuilder.Styles.Contains(style.Trim().ToLowerInvariant().Replace('-', '_')))
        {
            return null;
        }

        var names = state.Library.Names(LibraryKind.Style);
        if (!names.Contains(style.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ZeyoCutException($"unknown caption style '{style}'; use {string.Join(", ", CaptionBuilder.Styles)}{(names.Count == 0 ? " (or save a library style with save_style)" : $" or a library style: {string.Join(", ", names)}")}");
        }

        return state.Library.Load<TextStyle>(LibraryKind.Style, style);
    }

    [McpServerTool(Name = "export_srt")]
    [Description("Write the captions (the text layers under the caption group) as an .srt subtitle file to exports/<fileName>, with timeline times.")]
    public static string ExportSrt(
        EditorState state,
        [Description("Output file name, e.g. \"talk.srt\" (no folders).")] string fileName,
        [Description("Caption group id. Default captions.")] string groupId = CaptionBuilder.DefaultGroup) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"talk.srt\" without folders");
        }

        var name = fileName.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            name += ".srt";
        }
        else if (!extension.Equals(".srt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ZeyoCutException($"export_srt writes .srt files (got '{extension}')");
        }

        var session = state.Session;
        var cues = session.Read(p => CaptionBuilder.GroupCues(p, groupId));
        var output = Path.Combine(session.Directory, ProjectStore.ExportsFolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, CaptionBuilder.Srt(cues), new UTF8Encoding(false));
        return $"wrote {cues.Count} subtitle(s) to {output} ({Num(cues[0].Start)}–{Num(cues[^1].End)}s)";
    });
}
