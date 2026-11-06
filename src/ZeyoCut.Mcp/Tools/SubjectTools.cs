// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class SubjectTools
{
    // An image whose drawings have at least this share of fully transparent pixels is its own subject.
    public const double TransparentShare = 0.01;

    [McpServerTool(Name = "text_behind_subject")]
    [Description("Put text behind the subject of a clip or picture, like a title behind a presenter (one undo step): the text stays in front of the background, and the subject is drawn again above it, cut out. For a video layer this adds a subject cutout layer (type cutout) above the texts, cut out with aiVideo by default (or chroma for a green screen); for an image layer with aiImage by default (or edgeFill or chroma); the methods work as in remove_background. The cutout always follows its source and the pieces splits made of it on the same track: timing, trims, speed, transforms, effects and keyframes carry over, and its own settings are only name, zIndex, visible and opacity (keyframes too). If the subject is already a layer of its own (a removed background, a transparent picture, or a group of layers), no cutout is needed and the texts move directly below it. Texts drawn below the clip are lifted just above it. AI mattes are computed in the background as a matte_N job; until then the text shows in front and exports refuse. Running it again on the same clip reuses its cutout (with a new method if you give one). Check with render_frame (layerId=<cutout> shows the subject alone); text_occlusion measures how much of a text is covered.")]
    public static string TextBehindSubject(
        EditorState state,
        [Description("The video (or image) layer with the subject in it, or a group whose layers are the subject.")] string videoLayerId,
        [Description("Text layers to put behind the subject; a group (like captions) stands for the text layers under it.")] string[] textLayerIds,
        [Description("How the cutout finds the subject: aiVideo (default for video), aiImage (default for images), chroma or edgeFill. Default for an existing cutout: unchanged.")] string? method = null,
        [Description("chroma: the screen color; edgeFill: the background color. #RRGGBB. Default: picked from the picture.")] string? color = null,
        [Description("chroma or edgeFill: 0-1. Default 0.4 (chroma), 0.1 (edgeFill).")] double? tolerance = null,
        [Description("AI methods: start even if computing the matte would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first.")] bool confirmLongJob = false) => Guard(() =>
    {
        var session = state.Session;
        var directory = session.Directory;
        var project = session.Snapshot();
        var kind = method is null ? (MatteMethod?)null : EnumNames.Parse<MatteMethod>(method, "method");
        var notes = new List<string>();
        var transparent = false;
        Matte? matte = null;
        Matte? kept = null;
        IMattable? subject = null;
        lock (state.RenderGate)
        {
            var renderer = state.Renderer;
            if (ProjectEditor.GetLayer(project, videoLayerId, "videoLayerId") is ImageLayer { Drawings.Count: > 0, Matte: null } image)
            {
                transparent = image.Drawings.Values.Distinct(StringComparer.Ordinal)
                    .All(id => Mattes.RemovedShare(renderer.DrawingImage(ProjectEditor.GetAsset(project, id, AssetType.Image))) >= TransparentShare);
            }

            var target = SubjectEditor.Resolve(project, videoLayerId, textLayerIds, transparent);
            if (target.OwnSubject is not null)
            {
                if (kind is not null || color is not null || tolerance is not null)
                {
                    notes.Add("method, color and tolerance aren't used, since no cutout is needed");
                }
            }
            else
            {
                subject = (IMattable)target.Subject;
                if (target.Existing?.Matte is { } existing && kind is null && color is null && tolerance is null)
                {
                    kind = existing.Method;
                    kept = existing;
                }
                else
                {
                    kind ??= target.Existing?.Matte?.Method ?? (subject is VideoLayer ? MatteMethod.AiVideo : MatteMethod.AiImage);
                    var picked = kind == MatteMethod.Chroma && color is null ? MatteTools.PickKey(renderer, project, subject, notes) : color;
                    matte = MatteEditor.Build(subject, kind.Value, new MatteValues(picked, tolerance, null, null));
                    if (!MatteFiles.IsAi(kind.Value))
                    {
                        notes.AddRange(MatteTools.Preview(renderer, project, subject, matte));
                    }
                }
            }
        }

        var ai = subject is not null && MatteFiles.IsAi(kind!.Value);
        var ffmpeg = ai && kind == MatteMethod.AiVideo ? state.RequireFfmpeg() : null;
        if (ai && (matte ?? kept) is { } planned)
        {
            var targets = MatteTools.AiTargets(project, directory, subject!, subject!, planned);
            if (SystemTools.NotStarted(MatteTools.Estimate(state, kind!.Value, targets) is { } estimate ? [estimate] : [], confirmLongJob, "text_behind_subject") is { } notStarted)
            {
                return notStarted;
            }
        }

        var change = session.Mutate($"text_behind_subject {videoLayerId}", p => SubjectEditor.Apply(p, videoLayerId, textLayerIds, transparent, matte));
        var lines = change.Notes.Concat(notes).ToList();
        if (change.Cutout is { } cutout)
        {
            if (ai)
            {
                var after = session.Snapshot();
                var made = (CutoutLayer)after.FindLayer(cutout.Id)!;
                var targets = MatteTools.AiTargets(after, directory, made, (IMattable)after.FindLayer(subject!.Id)!, made.Matte!);
                lines.AddRange(MatteTools.StartAiMatte(state, cutout.Id, cutout: true, kind!.Value, targets, ffmpeg, directory));
            }

            lines.Add($"check it with render_frame (layerId=\"{cutout.Id}\" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of each text it covers");
        }

        return change.Summary + "\n" + string.Join("\n", lines.Select(l => "- " + l));
    });

    [McpServerTool(Name = "text_occlusion")]
    [Description("Measure how much of a text layer is covered by what is drawn above it (a subject cutout from text_behind_subject, or any other layer), frame by frame: the min, max and average share of the text hidden, and the frames in runs of equal values (every frame is saved to renders/text_occlusion_<id>_<first>-<last>.json). A half-transparent layer counts half; frames where the text isn't shown are left out of the numbers. Default range: the text's own time. Up to 150 frames answers directly; longer ranges run in the background and return a jobId (occlusion_N) for get_job_status.")]
    public static string TextOcclusion(
        EditorState state,
        [Description("Text layer id.")] string textLayerId,
        [Description("First time to measure, seconds. Default: the text's start.")] double? startTime = null,
        [Description("End of the range (not included), seconds. Default: the text's end.")] double? endTime = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var layer = ProjectEditor.GetLayer(project, textLayerId, "text layer");
        if (layer is not TextLayer text)
        {
            throw new ZeyoCutException($"'{layer.Id}' is a {layer.TypeName} layer; text_occlusion measures text layers");
        }

        var directory = session.Directory;
        if (MatteFiles.Pending(project, directory) is { Count: > 0 } pending)
        {
            throw new ZeyoCutException(
                $"background removal isn't finished: {string.Join(", ", pending.Take(5).Select(MatteFiles.Describe))}; wait for its matte job (get_job_status), then measure");
        }

        var (first, last) = Occlusion.Frames(project, text, startTime, endTime);
        string Run(Action<double>? progress, CancellationToken cancellationToken)
        {
            using var renderer = new FrameRenderer(directory, () => state.RequireFfmpeg().Ffmpeg, streamVideo: true);
            var result = Occlusion.Run(renderer, project, text.Id, first, last, progress, cancellationToken);
            return WithWarnings(result.Describe(), renderer.TakeWarnings());
        }

        var frames = last - first + 1;
        if (frames <= CheckTools.DirectFrames)
        {
            return Run(null, CancellationToken.None);
        }

        var job = state.Jobs.Start("occlusion", $"text_occlusion {text.Id} f{first}–f{last}", (job, ct) => Run(p => job.Report(p, "rendering frames"), ct));
        return $"measuring {frames} frames (f{first}–f{last}) of '{text.Id}' in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });
}
