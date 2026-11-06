// SPDX-License-Identifier: MIT

namespace ZeyoCut.Render.Video;

// What describe_clip asks a provider to watch: a stretch of a video asset's file, in source seconds.
// For a layer, the tool has already turned its timeline range into source seconds (trim and speed);
// LayerId is there for context.
public sealed record ClipRequest(string AssetId, string? LayerId, string FilePath, double Start, double End);

// One thing that happens in the clip, at Time in source seconds of the asset.
public sealed record ClipMoment(double Time, string Description);

// A video-understanding service behind describe_clip (Gemini, with native video input, is planned).
// It sends the segment Start–End of FilePath to the service and returns what happens, with times in
// source seconds between Start and End. It runs inside a background job, so it should honor
// cancellation. To add one, implement this and list it in ZeyoCut.Mcp's ClipDescribers.
public interface IClipDescriber
{
    // The provider's name as written in the config, e.g. "gemini".
    string Provider { get; }

    Task<IReadOnlyList<ClipMoment>> DescribeAsync(ClipRequest request, CancellationToken cancellationToken);
}
