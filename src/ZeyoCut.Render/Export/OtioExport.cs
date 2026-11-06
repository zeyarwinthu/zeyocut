// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// An EditPlan as an OpenTimelineIO timeline (.otio, the otio_json format): a Track.1 per track with
// Gap.1 and Clip.2 children, every time a RationalTime.1 at the project's frame rate. A clip's
// source_range is the stretch of media it starts from and its length on the timeline, with a
// LinearTimeWarp.1 for its speed; the markers are on the stack.
public static class OtioExport
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Write(EditPlan plan, string path)
    {
        var temp = path + ".partial";
        File.WriteAllText(temp, Json(plan).ToJsonString(Indented), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static JsonObject Json(EditPlan plan)
    {
        var project = plan.Project;
        var fps = project.Fps;
        return new JsonObject
        {
            ["OTIO_SCHEMA"] = "Timeline.1",
            ["metadata"] = new JsonObject { ["zeyocut"] = new JsonObject { ["width"] = project.Width, ["height"] = project.Height, ["fps"] = fps } },
            ["name"] = project.Name,
            ["global_start_time"] = Time(0, fps),
            ["tracks"] = new JsonObject
            {
                ["OTIO_SCHEMA"] = "Stack.1",
                ["metadata"] = new JsonObject(),
                ["name"] = "tracks",
                ["source_range"] = null,
                ["effects"] = new JsonArray(),
                ["markers"] = new JsonArray(plan.Markers.Select(m => (JsonNode)Marker(m, fps)).ToArray()),
                ["enabled"] = true,
                ["color"] = null,
                ["children"] = new JsonArray(plan.Tracks.Select(t => (JsonNode)Track(t, fps)).ToArray()),
            },
        };
    }

    private static JsonObject Track(PlannedTrack track, int fps)
    {
        var children = new JsonArray();
        var at = 0.0;
        foreach (var clip in track.Clips)
        {
            if (clip.Start - at > 1e-6)
            {
                children.Add(new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Gap.1",
                    ["metadata"] = new JsonObject(),
                    ["name"] = "",
                    ["source_range"] = Range(0, clip.Start - at, fps),
                    ["effects"] = new JsonArray(),
                    ["markers"] = new JsonArray(),
                    ["enabled"] = true,
                    ["color"] = null,
                });
            }

            children.Add(Clip(clip, track.Prerendered, fps));
            at = Math.Max(at, clip.Start) + clip.Length;
        }

        return new JsonObject
        {
            ["OTIO_SCHEMA"] = "Track.1",
            ["metadata"] = new JsonObject(),
            ["name"] = track.Name,
            ["source_range"] = null,
            ["effects"] = new JsonArray(),
            ["markers"] = new JsonArray(),
            ["enabled"] = true,
            ["color"] = null,
            ["children"] = children,
            ["kind"] = track.Audio ? "Audio" : "Video",
        };
    }

    private static JsonObject Clip(PlannedClip clip, bool prerendered, int fps)
    {
        var effects = new JsonArray();
        if (Math.Abs(clip.Speed - 1) > 1e-9)
        {
            effects.Add(new JsonObject
            {
                ["OTIO_SCHEMA"] = "LinearTimeWarp.1",
                ["metadata"] = new JsonObject(),
                ["name"] = "speed",
                ["effect_name"] = "LinearTimeWarp",
                ["enabled"] = true,
                ["time_scalar"] = clip.Speed,
            });
        }

        return new JsonObject
        {
            ["OTIO_SCHEMA"] = "Clip.2",
            ["metadata"] = new JsonObject { ["zeyocut"] = new JsonObject { ["layer"] = clip.LayerId, ["prerendered"] = prerendered } },
            ["name"] = prerendered ? $"{clip.LayerId} (prerendered)" : clip.LayerId,
            ["source_range"] = Range(clip.From, clip.Length, fps),
            ["effects"] = effects,
            ["markers"] = new JsonArray(),
            ["enabled"] = true,
            ["color"] = null,
            ["media_references"] = new JsonObject
            {
                ["DEFAULT_MEDIA"] = new JsonObject
                {
                    ["OTIO_SCHEMA"] = "ExternalReference.1",
                    ["metadata"] = new JsonObject(),
                    ["name"] = Path.GetFileName(clip.Media),
                    ["available_range"] = Range(0, clip.MediaLength, fps),
                    ["available_image_bounds"] = null,
                    ["target_url"] = new Uri(clip.Media).AbsoluteUri,
                },
            },
            ["active_media_reference_key"] = "DEFAULT_MEDIA",
        };
    }

    private static JsonObject Marker(Marker marker, int fps) => new()
    {
        ["OTIO_SCHEMA"] = "Marker.2",
        ["metadata"] = new JsonObject { ["zeyocut"] = new JsonObject { ["id"] = marker.Id, ["kind"] = marker.Kind == MarkerKind.Chapter ? "chapter" : "scene" } },
        ["name"] = marker.Name,
        ["color"] = marker.Kind == MarkerKind.Chapter ? "GREEN" : "BLUE",
        ["marked_range"] = Range(marker.Time, 0, fps),
        ["comment"] = "",
    };

    private static JsonObject Range(double start, double length, int fps) => new()
    {
        ["OTIO_SCHEMA"] = "TimeRange.1",
        ["duration"] = Time(length, fps),
        ["start_time"] = Time(start, fps),
    };

    // Seconds as a count of frames, kept to a millionth of a frame.
    private static JsonObject Time(double seconds, int fps) => new()
    {
        ["OTIO_SCHEMA"] = "RationalTime.1",
        ["rate"] = (double)fps,
        ["value"] = Math.Round(seconds * fps, 6),
    };
}
