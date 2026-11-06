// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Text;

namespace ZeyoCut.Mcp.Demo;

public static class DemoBuilder
{
    public const string ExportFileName = "tora_jump.mp4";
    public const double TakeoffTime = 0.9;
    public const double ApexTime = 1.35;
    public const double LandingTime = 1.8;
    public const double GroundY = 605;
    public const double ApexY = 360;
    public const double StartX = 780;
    public const double EndX = 980;

    private const string Linear = "linear";
    private const string EaseIn = "easeIn";
    private const string EaseOut = "easeOut";
    private const string EaseInOut = "easeInOut";
    private const string EaseOutBack = "easeOutBack";

    public static Project Build(string projectDirectory)
    {
        var directory = Path.GetFullPath(projectDirectory);
        var session = ProjectSession.Create(directory, new Project
        {
            Name = "Tora Jumps",
            Width = 1280,
            Height = 720,
            Fps = 30,
            Duration = 3,
            BackgroundColor = "#BFE6FF",
        });

        var source = Path.Combine(Path.GetTempPath(), "zeyocut-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            var files = new (string Id, string Path)[]
            {
                ("tora_body", DemoArt.Body(Path.Combine(source, "tora_body.png"))),
                ("tora_head", DemoArt.Head(Path.Combine(source, "tora_head.png"), happy: false)),
                ("tora_head_happy", DemoArt.Head(Path.Combine(source, "tora_head_happy.png"), happy: true)),
                ("tora_tail", DemoArt.Tail(Path.Combine(source, "tora_tail.png"))),
                ("shadow", DemoArt.Shadow(Path.Combine(source, "shadow.png"))),
                ("ground", DemoArt.Ground(Path.Combine(source, "ground.png"))),
                ("clouds", DemoArt.Clouds(Path.Combine(source, "clouds.png"))),
                ("boing", Tone(Path.Combine(source, "boing.wav"), p => ToneGenerator.WriteBoing(p))),
                ("thud", Tone(Path.Combine(source, "thud.wav"), p => ToneGenerator.WriteTone(p, 110, 0.3, 0.5))),
            };

            using var renderer = new FrameRenderer(directory);
            var probe = new AssetProbe(ToneGenerator.Duration);
            session.Mutate("build demo", p =>
            {
                foreach (var (id, file) in files)
                {
                    AssetImporter.Import(p, directory, file, id, probe);
                }

                AddLayers(p, new TextMeasurer(renderer, p));
                KeyframeEditor.SetKeyframes(p, Keyframes());
                return p.Layers.Count;
            });
        }
        finally
        {
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return session.Snapshot();
    }

    private static void AddLayers(Project p, ITextMeasurer measurer)
    {
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "clouds", Drawings = One("clouds"), AnchorX = 0, AnchorY = 0, X = 0, Y = 0, ZIndex = 1 });
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "ground", Drawings = One("ground"), AnchorX = 0, AnchorY = 0, X = 0, Y = 590, ZIndex = 2 });
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "shadow", Drawings = One("shadow"), X = StartX, Y = 606, Opacity = 0.35, ZIndex = 3 });

        ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "tora",
            Name = "Tora rig root",
            X = StartX,
            Y = GroundY,
            ScaleX = 0.85,
            ScaleY = 0.85,
            ZIndex = 8,
        });
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "tora_body",
            Name = "Tora body",
            ParentId = "tora",
            Drawings = One("tora_body"),
            AnchorX = 110,
            AnchorY = 234,
            X = 0,
            Y = 0,
            ZIndex = 10,
        });
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "tora_head",
            Name = "Tora head",
            ParentId = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "tora_head", ["happy"] = "tora_head_happy" },
            AnchorX = 120,
            AnchorY = 196,
            X = 110,
            Y = 26,
            ZIndex = 11,
        });
        ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "tora_tail",
            Name = "Tora tail",
            ParentId = "tora_body",
            Drawings = One("tora_tail"),
            AnchorX = 164,
            AnchorY = 72,
            X = 58,
            Y = 176,
            ZIndex = 9,
        });

        ProjectEditor.AddTextLayer(p, new TextLayerOptions
        {
            Id = "title",
            Text = "Tora Jumps!",
            FontFamily = "Segoe UI",
            Bold = true,
            FontSize = 88,
            Color = "#FFFFFF",
            StrokeColor = "#3B2A1A",
            StrokeWidth = 8,
            X = 340,
            Y = 150,
            ZIndex = 20,
        }, measurer);
        ProjectEditor.AddTextLayer(p, new TextLayerOptions
        {
            Id = "subtitle",
            Text = "トラのジャンプ",
            FontFamily = "Yu Gothic",
            Bold = true,
            FontSize = 40,
            Color = "#3B2A1A",
            X = 340,
            Y = 238,
            ZIndex = 21,
        }, measurer);

        ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = "boing", Id = "sfx_boing", Start = TakeoffTime, Volume = 0.8 });
        ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = "thud", Id = "sfx_thud", Start = LandingTime, Volume = 1.0, FadeOut = 0.1 });
    }

    private static List<KeyframeEntry> Keyframes()
    {
        var keys = new List<KeyframeEntry>();

        void Track(string layer, string property, params (double Time, double Value, string Easing)[] points)
        {
            foreach (var (time, value, easing) in points)
            {
                keys.Add(new KeyframeEntry(layer, property, time, JsonSerializer.SerializeToElement(value), easing));
            }
        }

        Track("tora", "x", (TakeoffTime, StartX, Linear), (LandingTime, EndX, Linear));
        Track("tora", "y", (TakeoffTime, GroundY, EaseOut), (ApexTime, ApexY, EaseIn), (LandingTime, GroundY, Linear));
        Track("tora", "rotation", (TakeoffTime, 0, EaseInOut), (1.15, 5, EaseInOut), (1.7, -3, EaseInOut), (1.85, 0, Linear));
        Track("tora_body", "scaleY",
            (0.5, 1, EaseInOut), (TakeoffTime, 0.7, EaseOut), (1.0, 1.22, EaseInOut), (ApexTime, 0.97, EaseIn),
            (1.75, 1.15, Linear), (1.83, 0.72, EaseOutBack), (2.3, 1, Linear));
        Track("tora_body", "scaleX",
            (0.5, 1, EaseInOut), (TakeoffTime, 1.25, EaseOut), (1.0, 0.84, EaseInOut), (ApexTime, 1.03, EaseIn),
            (1.75, 0.9, Linear), (1.83, 1.25, EaseOutBack), (2.3, 1, Linear));

        Track("tora_head", "y",
            (0.55, 26, EaseOut), (TakeoffTime, 34, EaseIn), (1.0, 40, EaseOut), (1.3, 18, EaseInOut),
            (1.75, 22, EaseIn), (1.83, 44, EaseOutBack), (2.35, 26, Linear));
        Track("tora_head", "rotation",
            (0.55, 0, EaseInOut), (TakeoffTime, 4, EaseOut), (1.05, -10, EaseInOut), (1.4, 5, EaseInOut),
            (LandingTime, -2, EaseOut), (1.9, 8, EaseOutBack), (2.4, 0, Linear));
        keys.Add(new KeyframeEntry("tora_head", "drawing", 0, JsonSerializer.SerializeToElement("normal")));
        keys.Add(new KeyframeEntry("tora_head", "drawing", 0.95, JsonSerializer.SerializeToElement("happy")));
        keys.Add(new KeyframeEntry("tora_head", "drawing", 2.15, JsonSerializer.SerializeToElement("normal")));

        Track("tora_tail", "rotation",
            (0, 0, EaseInOut), (0.3, 12, EaseInOut), (0.6, -6, EaseInOut), (TakeoffTime, 10, EaseInOut),
            (1.05, -24, EaseInOut), (1.3, 0, EaseInOut), (1.6, 18, EaseInOut), (LandingTime, 26, EaseOut),
            (1.9, -22, EaseInOut), (2.15, 14, EaseInOut), (2.4, -8, EaseInOut), (2.65, 6, EaseInOut), (2.9, 0, Linear));

        Track("shadow", "x", (TakeoffTime, StartX, Linear), (LandingTime, EndX, Linear));
        foreach (var axis in new[] { "scaleX", "scaleY" })
        {
            Track("shadow", axis,
                (0.5, 1, EaseInOut), (TakeoffTime, 1.15, EaseOut), (ApexTime, 0.55, EaseIn), (LandingTime, 1, Linear),
                (1.83, 1.18, EaseOutBack), (2.3, 1, Linear));
        }

        Track("shadow", "opacity", (TakeoffTime, 0.35, EaseOut), (ApexTime, 0.12, EaseIn), (LandingTime, 0.35, Linear));

        Track("clouds", "x", (0, 0, Linear), (3, -80, Linear));

        Track("title", "scaleX", (0, 0.3, EaseOutBack), (0.5, 1, Linear));
        Track("title", "scaleY", (0, 0.3, EaseOutBack), (0.5, 1, Linear));
        Track("title", "opacity", (0, 0, EaseOut), (0.15, 1, Linear));
        Track("subtitle", "y", (0.25, 258, EaseOut), (0.7, 238, Linear));
        Track("subtitle", "opacity", (0.25, 0, EaseOut), (0.7, 1, Linear));
        return keys;
    }

    private static Dictionary<string, string> One(string asset) => new() { ["normal"] = asset };

    private static string Tone(string path, Action<string> write)
    {
        write(path);
        return path;
    }
}
