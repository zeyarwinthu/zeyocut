// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// An EditPlan as Final Cut Pro XML (FCPXML 1.9), which DaVinci Resolve also reads. The first video
// track is the primary storyline (the spine), with gaps where it has no clip, through to the
// project's end; every other track's clips are connected to the spine item under their start, on
// lanes 1, 2, … above it (video and prerendered layers) and -1, -2, … below it (sound). A picture
// clip uses only the video of its file and a sound clip only the audio. Speed is a linear timeMap.
// Times are whole frames of the project.
public static class FcpxmlExport
{
    public const string Version = "1.9";

    private sealed class SpineItem(XElement element, long offset, long duration, long start)
    {
        public XElement Element { get; } = element;

        public long Offset { get; } = offset;

        public long Duration { get; } = duration;

        public long Start { get; } = start;

        public List<XElement> Connected { get; } = [];

        public List<XElement> Markers { get; } = [];
    }

    public static void Write(EditPlan plan, string path)
    {
        var temp = path + ".partial";
        using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            Document(plan).Save(writer);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static XDocument Document(EditPlan plan)
    {
        var project = plan.Project;
        var fps = project.Fps;
        var total = (long)FrameMath.FrameCount(project);
        long Frames(double seconds) => (long)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
        string Time(long frames) => frames == 0 ? "0s" : $"{frames.ToString(CultureInfo.InvariantCulture)}/{fps.ToString(CultureInfo.InvariantCulture)}s";
        var frameDuration = $"1/{fps.ToString(CultureInfo.InvariantCulture)}s";

        var resources = new XElement("resources");
        var formats = new Dictionary<string, string>(StringComparer.Ordinal);
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Id() => $"r{resources.Elements().Count() + 1}";

        string Format(int width, int height, string duration)
        {
            var key = $"{width}x{height}@{duration}";
            if (!formats.TryGetValue(key, out var id))
            {
                id = Id();
                resources.Add(new XElement("format", new XAttribute("id", id), new XAttribute("frameDuration", duration), new XAttribute("width", width), new XAttribute("height", height), new XAttribute("colorSpace", "1-1-1 (Rec. 709)")));
                formats[key] = id;
            }

            return id;
        }

        var sequenceFormat = Format(project.Width, project.Height, frameDuration);

        // The file's asset: its picture's size and frame rate, whether it has sound, its length.
        string Asset(PlannedClip clip, bool prerendered)
        {
            if (assets.TryGetValue(clip.Media, out var id))
            {
                return id;
            }

            var source = prerendered ? null : project.FindLayer(clip.LayerId) switch
            {
                VideoLayer video => project.FindAsset(video.Asset),
                AudioLayer audio => project.FindAsset(audio.Asset),
                _ => null,
            };
            var hasVideo = prerendered || source?.Type == AssetType.Video;
            var hasAudio = !prerendered && (source?.Type == AssetType.Audio || source?.HasAudio == true);
            string? format = null;
            if (hasVideo)
            {
                var (numerator, denominator) = source is null ? (fps, 1L) : VideoTiming.ParseRate(source.FrameRate);
                format = Format(source?.Width ?? project.Width, source?.Height ?? project.Height, $"{denominator.ToString(CultureInfo.InvariantCulture)}/{numerator.ToString(CultureInfo.InvariantCulture)}s");
            }

            id = Id();
            var asset = new XElement("asset",
                new XAttribute("id", id),
                new XAttribute("name", Path.GetFileNameWithoutExtension(clip.Media)),
                new XAttribute("start", "0s"),
                new XAttribute("duration", Time(Frames(clip.MediaLength))),
                new XAttribute("hasVideo", hasVideo ? "1" : "0"),
                new XAttribute("hasAudio", hasAudio ? "1" : "0"));
            if (format is not null)
            {
                asset.Add(new XAttribute("format", format));
            }

            if (hasAudio)
            {
                asset.Add(new XAttribute("audioSources", "1"), new XAttribute("audioChannels", "2"), new XAttribute("audioRate", "48000"));
            }

            asset.Add(new XElement("media-rep", new XAttribute("kind", "original-media"), new XAttribute("src", new Uri(clip.Media).AbsoluteUri)));
            resources.Add(asset);
            assets[clip.Media] = id;
            return id;
        }

        // A clip whose own time starts at start; a speed other than 1 maps it to the media's time.
        XElement Clip(PlannedClip clip, bool prerendered, bool audio, long offset, out long start, out long duration)
        {
            var first = Frames(clip.Start);
            duration = Math.Max(1, Frames(clip.Start + clip.Length) - first);
            var retimed = Math.Abs(clip.Speed - 1) > 1e-9;
            start = Frames(retimed ? clip.From / clip.Speed : clip.From);
            var reference = Asset(clip, prerendered);
            var element = new XElement("asset-clip",
                new XAttribute("ref", reference),
                new XAttribute("offset", Time(offset)),
                new XAttribute("name", prerendered ? $"{clip.LayerId} (prerendered)" : clip.LayerId),
                new XAttribute("start", Time(start)),
                new XAttribute("duration", Time(duration)),
                new XAttribute("tcFormat", "NDF"));
            var withSound = resources.Elements("asset").First(a => (string?)a.Attribute("id") == reference).Attribute("hasAudio")!.Value == "1";
            var withPicture = resources.Elements("asset").First(a => (string?)a.Attribute("id") == reference).Attribute("hasVideo")!.Value == "1";
            if (withSound && withPicture)
            {
                element.Add(new XAttribute("srcEnable", audio ? "audio" : "video"));
            }

            if (retimed)
            {
                element.Add(new XElement("timeMap",
                    new XElement("timept", new XAttribute("time", "0s"), new XAttribute("value", "0s"), new XAttribute("interp", "linear")),
                    new XElement("timept", new XAttribute("time", Time(Frames(clip.MediaLength / clip.Speed))), new XAttribute("value", Time(Frames(clip.MediaLength))), new XAttribute("interp", "linear"))));
            }

            return element;
        }

        var spineTrack = plan.Tracks.FirstOrDefault(t => !t.Audio && !t.Prerendered);
        var spine = new List<SpineItem>();
        long at = 0;

        void Gap(long until)
        {
            if (until > at)
            {
                spine.Add(new SpineItem(new XElement("gap", new XAttribute("name", "Gap"), new XAttribute("offset", Time(at)), new XAttribute("start", Time(at)), new XAttribute("duration", Time(until - at))), at, until - at, at));
                at = until;
            }
        }

        foreach (var clip in spineTrack?.Clips ?? [])
        {
            Gap(Frames(clip.Start));
            var element = Clip(clip, false, false, at, out var start, out var duration);
            spine.Add(new SpineItem(element, at, duration, start));
            at += duration;
        }

        Gap(total);

        SpineItem Under(long frame) =>
            spine.FirstOrDefault(s => frame >= s.Offset && frame < s.Offset + s.Duration) ?? spine[^1];

        var videoLane = 0;
        var audioLane = 0;
        foreach (var track in plan.Tracks.Where(t => !ReferenceEquals(t, spineTrack)))
        {
            var lane = track.Audio ? -++audioLane : ++videoLane;
            foreach (var clip in track.Clips)
            {
                var frame = Frames(clip.Start);
                var parent = Under(frame);
                var element = Clip(clip, track.Prerendered, track.Audio, parent.Start + (frame - parent.Offset), out _, out _);
                element.Add(new XAttribute("lane", lane));
                parent.Connected.Add(element);
            }
        }

        foreach (var marker in plan.Markers)
        {
            var frame = Math.Min(Frames(marker.Time), total - 1);
            var parent = Under(frame);
            var start = Time(parent.Start + (frame - parent.Offset));
            parent.Markers.Add(marker.Kind == MarkerKind.Chapter
                ? new XElement("chapter-marker", new XAttribute("start", start), new XAttribute("duration", frameDuration), new XAttribute("value", marker.Name), new XAttribute("posterOffset", "0s"))
                : new XElement("marker", new XAttribute("start", start), new XAttribute("duration", frameDuration), new XAttribute("value", marker.Name)));
        }

        foreach (var item in spine)
        {
            item.Element.Add(item.Connected);
            item.Element.Add(item.Markers);
        }

        var sequence = new XElement("sequence",
            new XAttribute("format", sequenceFormat),
            new XAttribute("duration", Time(total)),
            new XAttribute("tcStart", "0s"),
            new XAttribute("tcFormat", "NDF"),
            new XAttribute("audioLayout", "stereo"),
            new XAttribute("audioRate", "48k"),
            new XElement("spine", spine.Select(s => s.Element)));
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("fcpxml", null, null, null),
            new XElement("fcpxml",
                new XAttribute("version", Version),
                resources,
                new XElement("library",
                    new XElement("event", new XAttribute("name", "ZeyoCut"),
                        new XElement("project", new XAttribute("name", project.Name), sequence)))));
    }
}
