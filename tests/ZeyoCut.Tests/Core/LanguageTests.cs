// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Tests.Core;

public class LanguageTests
{
    [Fact]
    public void SubtitleFileNamesEndingInALanguageGiveIt()
    {
        Assert.Equal("ja", Languages.FromFileName("talk.ja.srt"));
        Assert.Equal("pt-BR", Languages.FromFileName(@"C:\videos\talk_pt-BR.srt"));
        Assert.Equal("zh-Hant", Languages.FromFileName("movie.zh-Hant.srt"));
        Assert.Equal("my", Languages.FromFileName("episode 3_my.srt"));
        Assert.Null(Languages.FromFileName("talk.srt"));
        Assert.Null(Languages.FromFileName("my.srt"));
        Assert.Null(Languages.FromFileName("talk_new.srt"));
        Assert.Null(Languages.FromFileName("talk_BR.srt"));
        Assert.Null(Languages.FromFileName("talk en.srt"));
    }

    // A clip whose sound is English, a Japanese dub, a caption group per language, an untagged logo
    // and an untagged badge on the clip.
    private static Project Bilingual()
    {
        var project = new Project { Name = "demo", Width = 640, Height = 360, Fps = 30, Duration = 10 };
        project.Assets.Add(new Asset
        {
            Id = "clip",
            Type = AssetType.Video,
            File = "assets/clip.mp4",
            Width = 640,
            Height = 360,
            FrameRate = "30",
            FrameCount = 300,
            Duration = 10,
            HasAudio = true,
            Codec = "h264",
        });
        project.Assets.Add(new Asset { Id = "dub_ja", Type = AssetType.Audio, File = "assets/dub_ja.wav", Duration = 10 });
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 100, Height = 100 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "talk", Language = "EN" });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "dub_ja", Id = "dub", Role = "voice", Language = "ja" });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "captions_ja", Language = "ja" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Text = "こんにちは", Id = "cap_ja_1", ParentId = "captions_ja" });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "captions_en", Language = "en" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Text = "Hello", Id = "cap_en_1", ParentId = "captions_en" });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" } });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Text = "LIVE", Id = "badge", ParentId = "talk" });
        return project;
    }

    private static string[] Ids(Project project) => project.Layers.Select(l => l.Id).ToArray();

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" my ", "my")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("pt_BR", "pt-BR")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    [InlineData("es-419", "es-419")]
    [InlineData("fil", "fil")]
    public void LanguageCodesAreNormalized(string code, string expected)
    {
        Assert.True(Languages.TryNormalize(code, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, Languages.Normalize(code));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("e")]
    [InlineData("en-")]
    [InlineData("12")]
    [InlineData("en-USA-x")]
    [InlineData("")]
    public void OtherTextIsNotALanguageCode(string code)
    {
        Assert.False(Languages.TryNormalize(code, out _));
        var error = Assert.Throws<ZeyoCutException>(() => Languages.Normalize(code));
        Assert.Equal($"language '{code}' isn't a language code; use one such as en, ja, my or pt-BR", error.Message);
    }

    [Fact]
    public void ALanguageFindsItsFontOrTheNearestShorterTagsFont()
    {
        Assert.Equal("Myanmar Text", Languages.FontFor(Languages.DefaultFonts, "my"));
        Assert.Equal("Yu Gothic", Languages.FontFor(Languages.DefaultFonts, "ja-JP"));
        Assert.Null(Languages.FontFor(Languages.DefaultFonts, "en"));
        Assert.Null(Languages.FontFor(Languages.DefaultFonts, null));

        var fonts = new Dictionary<string, string> { ["zh"] = "Microsoft YaHei", ["zh-Hant"] = "Microsoft JhengHei" };
        Assert.Equal("Microsoft JhengHei", Languages.FontFor(fonts, "zh-Hant-TW"));
        Assert.Equal("Microsoft JhengHei", Languages.FontFor(fonts, "zh-Hant"));
        Assert.Equal("Microsoft YaHei", Languages.FontFor(fonts, "zh-CN"));
    }

    [Fact]
    public void EachVersionHasItsOwnLayersAndTheUntaggedOnes()
    {
        var project = Bilingual();
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(["en", "ja"], Languages.Used(project));
        Assert.Equal("en", project.FindLayer("talk")!.Language);
        Assert.Equal("ja", Languages.VersionOf(project, project.FindLayer("cap_ja_1")!));
        Assert.Null(Languages.VersionOf(project, project.FindLayer("talk")!));
        Assert.Null(Languages.VersionOf(project, project.FindLayer("badge")!));
        Assert.Null(Languages.VersionOf(project, project.FindLayer("logo")!));

        var ja = ProjectJson.Clone(project);
        var japanese = Languages.Select(ja, "ja");
        Assert.Equal(["captions_en", "cap_en_1"], japanese.Removed);
        Assert.Equal(["talk"], japanese.Muted);
        Assert.Equal(["talk", "dub", "captions_ja", "cap_ja_1", "logo", "badge"], Ids(ja));
        Assert.True(ja.Layers.OfType<VideoLayer>().Single().Muted);
        Assert.Empty(ProjectValidator.Validate(ja));

        var en = ProjectJson.Clone(project);
        var english = Languages.Select(en, "en");
        Assert.Equal(["dub", "captions_ja", "cap_ja_1"], english.Removed);
        Assert.Empty(english.Muted);
        Assert.Equal(["talk", "captions_en", "cap_en_1", "logo", "badge"], Ids(en));
        Assert.False(en.Layers.OfType<VideoLayer>().Single().Muted);

        var fr = ProjectJson.Clone(project);
        var french = Languages.Select(fr, "fr");
        Assert.Equal(["dub", "captions_ja", "cap_ja_1", "captions_en", "cap_en_1"], french.Removed);
        Assert.Equal(["talk"], french.Muted);
        Assert.Equal(["talk", "logo", "badge"], Ids(fr));

        Assert.Equal(8, project.Layers.Count);
        Assert.False(project.Layers.OfType<VideoLayer>().Single().Muted);
    }

    [Fact]
    public void CutoutsOfLayersLeftOutGoToo()
    {
        var project = Bilingual();
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "art_ja", Language = "ja", Drawings = new Dictionary<string, string> { ["normal"] = "logo" } });
        project.Layers.Add(new CutoutLayer { Id = "art_ja_subject", Name = "art_ja_subject", Sources = ["art_ja"] });
        project.Layers.Add(new CutoutLayer { Id = "captions_ja_subject", Name = "captions_ja_subject", Sources = ["talk"], CaptionGroup = "captions_ja" });

        var en = ProjectJson.Clone(project);
        Assert.Equal(["dub", "captions_ja", "cap_ja_1", "art_ja", "art_ja_subject", "captions_ja_subject"], Languages.Select(en, "en").Removed);
        Assert.Empty(en.Layers.OfType<CutoutLayer>());

        var ja = ProjectJson.Clone(project);
        Assert.Equal(["captions_en", "cap_en_1"], Languages.Select(ja, "ja").Removed);
        Assert.Equal(["art_ja_subject", "captions_ja_subject"], ja.Layers.OfType<CutoutLayer>().Select(c => c.Id));
    }

    [Fact]
    public void ChooseTellsWhenAPreviewOrExportMixesLanguagesOrFindsNone()
    {
        var project = Bilingual();
        var all = Languages.Choose(ProjectJson.Clone(project), null, export: true);
        Assert.Null(all.Language);
        Assert.Equal("", all.Note);
        Assert.Equal(["this export includes the layers of every language (en, ja); give language for one version, or export_language_versions for one file per language"], all.Warnings);
        Assert.Equal(["this shows the layers of every language (en, ja); give language to see one version"], Languages.Choose(ProjectJson.Clone(project), " ", export: false).Warnings);

        var ja = Languages.Choose(ProjectJson.Clone(project), "JA", export: false);
        Assert.Equal("ja", ja.Language);
        Assert.Empty(ja.Warnings);
        Assert.Equal("; ja version, 2 layer(s) of other languages left out, sound of 'talk' muted", ja.Note);
        Assert.Equal("; en version, 3 layer(s) of other languages left out", Languages.Choose(ProjectJson.Clone(project), "en", export: true).Note);

        var fr = Languages.Choose(ProjectJson.Clone(project), "fr", export: false);
        Assert.Equal(["no layer is tagged fr, so this shows only the untagged layers; languages in the project: en, ja"], fr.Warnings);
        Assert.Equal(["no layer is tagged fr, so this export has only the untagged layers; languages in the project: en, ja"], Languages.Choose(ProjectJson.Clone(project), "fr", export: true).Warnings);

        var untagged = ProjectJson.Clone(project);
        foreach (var layer in untagged.Layers)
        {
            layer.Language = null;
        }

        var nothing = Languages.Choose(untagged, "ja", export: true);
        Assert.Equal(["no layer has a language tag, so language ja changes nothing; update_layer with language tags one"], nothing.Warnings);
        Assert.Equal("; ja version", nothing.Note);
        Assert.Empty(Languages.Choose(untagged, null, export: true).Warnings);

        var oneLanguage = ProjectJson.Clone(project);
        oneLanguage.FindLayer("talk")!.Language = "ja";
        oneLanguage.Layers.RemoveAll(l => l.Id is "captions_en" or "cap_en_1");
        Assert.Empty(Languages.Choose(oneLanguage, null, export: false).Warnings);

        var error = Assert.Throws<ZeyoCutException>(() => Languages.Choose(ProjectJson.Clone(project), "english", export: false));
        Assert.Equal("language 'english' isn't a language code; use one such as en, ja, my or pt-BR", error.Message);
    }

    [Fact]
    public void TheValidatorRejectsBadTagsAndTagsThatHideAChildForever()
    {
        var project = Bilingual();
        project.FindLayer("logo")!.Language = "english";
        Assert.Equal(["layer 'logo': language 'english' isn't a language code; use one such as en, ja, my or pt-BR"], ProjectValidator.Validate(project));

        project.FindLayer("logo")!.Language = "pt-br";
        Assert.Equal(["layer 'logo': write language 'pt-br' as pt-BR"], ProjectValidator.Validate(project));
        project.FindLayer("logo")!.Language = null;

        project.FindLayer("cap_ja_1")!.Language = "en";
        Assert.Equal(["layer 'cap_ja_1': its language en differs from ja on 'captions_ja', which it is part of; a child is only in its parent's version, so tag one of them or give it another parent"], ProjectValidator.Validate(project));
        project.FindLayer("cap_ja_1")!.Language = "ja";
        Assert.Empty(ProjectValidator.Validate(project));

        project.FindLayer("badge")!.Language = "ja";
        Assert.Empty(ProjectValidator.Validate(project));

        project.Layers.Add(new CutoutLayer { Id = "subject", Name = "subject", Sources = ["talk"], Language = "en" });
        project.Layers.Add(new RedactionLayer { Id = "blur", Name = "blur", Language = "ja" });
        var errors = ProjectValidator.Validate(project);
        Assert.Contains("layer 'subject': a subject cutout is in the versions its sources are in, so it has no language of its own; tag its sources", errors);
        Assert.Contains("layer 'blur': a redaction hides in every language's version, so it has no language", errors);
    }

    [Fact]
    public void UpdateLayerTagsAndUntagsLayers()
    {
        var project = Bilingual();
        ProjectEditor.UpdateLayer(project, "logo", new JsonObject { ["language"] = "PT_br" });
        Assert.Equal("pt-BR", project.FindLayer("logo")!.Language);
        Assert.Equal("pt-BR", ProjectJson.LayerToNode(project.FindLayer("logo")!)["language"]!.GetValue<string>());

        ProjectEditor.UpdateLayer(project, "logo", new JsonObject { ["Language"] = "" });
        Assert.Null(project.FindLayer("logo")!.Language);
        Assert.False(ProjectJson.LayerToNode(project.FindLayer("logo")!).ContainsKey("language"));

        var bad = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "logo", new JsonObject { ["language"] = "english" }));
        Assert.Equal("language 'english' isn't a language code; use one such as en, ja, my or pt-BR", bad.Message);

        var unknown = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "dub", new JsonObject { ["lang"] = "ja" }));
        Assert.Contains("visible, language,", unknown.Message);

        project.Layers.Add(new CutoutLayer { Id = "subject", Name = "subject", Sources = ["talk"] });
        var cutout = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "subject", new JsonObject { ["language"] = "ja" }));
        Assert.Equal("'subject': a subject cutout is in the versions its sources are in, so it has no language of its own; tag its sources", cutout.Message);

        var addBad = Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "dub_ja", Language = "japanese" }));
        Assert.Equal("language 'japanese' isn't a language code; use one such as en, ja, my or pt-BR", addBad.Message);

        var tree = LayerTree.Format(Bilingual());
        Assert.Contains("languages: en, ja", tree);
        Assert.Contains("lang=en (sound)", tree);
        Assert.Contains("captions_ja [image]  z=1  t=0–end  lang=ja  null (no drawings)", tree);
        Assert.Contains("dub [audio]  t=0–end  lang=ja  asset=dub_ja", tree);
    }
}
