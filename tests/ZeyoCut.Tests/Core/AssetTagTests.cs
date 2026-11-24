// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Tests.Core;

// Tags on assets: tag_assets, the editor's bins and the project file.
public class AssetTagTests
{
    private static Project Assets()
    {
        var project = new Project { Width = 320, Height = 180, Fps = 10, Duration = 2 };
        project.Assets.Add(new Asset { Id = "tora_body", Type = AssetType.Image, File = "assets/tora_body.png", Width = 220, Height = 240 });
        project.Assets.Add(new Asset { Id = "tora_head", Type = AssetType.Image, File = "assets/tora_head.png", Width = 240, Height = 208 });
        project.Assets.Add(new Asset { Id = "boing", Type = AssetType.Audio, File = "assets/boing.wav", Duration = 0.6 });
        return project;
    }

    [Fact]
    public void TagsAreAddedOnceAndTakenOffWhateverTheirCase()
    {
        var project = Assets();
        Assert.Equal("'tora_body' tags: none → Tora rig; 'tora_head' tags: none → Tora rig",
            AssetTags.Tag(project, ["tora_body", "tora_head"], ["  Tora   rig "], null));
        Assert.Equal("'tora_head' tags: Tora rig → Tora rig, Heads", AssetTags.Tag(project, ["tora_head"], ["TORA RIG", "Heads"], ["heads2"]));
        Assert.Equal(["Tora rig", "Heads"], project.FindAsset("tora_head")!.Tags);
        Assert.Equal(["Tora rig"], project.FindAsset("tora_body")!.Tags);
        Assert.Equal([("Heads", 1), ("Tora rig", 2)], AssetTags.All(project));
        Assert.True(AssetTags.Has(project.FindAsset("tora_body")!, "tora RIG"));

        // The last tag taken off leaves none at all.
        Assert.Equal("'tora_body' tags: Tora rig → none", AssetTags.Tag(project, ["tora_body"], null, ["tora rig"]));
        Assert.Null(project.FindAsset("tora_body")!.Tags);
        Assert.Null(project.FindAsset("boing")!.Tags);
    }

    [Fact]
    public void ATagMustBeShortAndPlainAndTheChangeReal()
    {
        var project = Assets();
        Assert.Equal("a tag can't be empty", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], ["  "], null)).Message);
        Assert.Equal($"tag '{new string('x', 41)}' is 41 characters; a tag has at most 40",
            Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], [new string('x', 41)], null)).Message);
        Assert.Equal("tag 'a\u0007b' has a control character", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], ["a\u0007b"], null)).Message);
        Assert.Equal("nothing to change; pass tags to add, to remove or both", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], [], null)).Message);
        Assert.Equal("tag 'SFX' is both added and removed", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], ["SFX"], ["sfx"])).Message);
        Assert.Equal("give at least one asset id", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, [], ["SFX"], null)).Message);
        Assert.Equal("asset 'thud' not found; assets: tora_body, tora_head, boing",
            Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing", "thud"], ["SFX"], null)).Message);
        Assert.Null(project.FindAsset("boing")!.Tags);

        AssetTags.Tag(project, ["boing"], ["SFX"], null);
        Assert.Equal("nothing changed; 'boing' has tags: SFX", Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing"], ["sfx"], ["Music"])).Message);
        Assert.Equal("nothing changed; the assets have those tags already, and not the ones to remove",
            Assert.Throws<ZeyoCutException>(() => AssetTags.Tag(project, ["boing", "tora_body"], null, ["Music"])).Message);
    }

    [Fact]
    public void TagsAreSavedOnlyWhenThereAreSomeAndCheckedOnLoad()
    {
        var project = Assets();
        new TagAssetsOperation(["boing"], ["SFX", "Cartoon"], []).Apply(project);
        var json = ProjectJson.Serialize(project);
        Assert.Equal(2, json.Split("\"tags\"").Length);
        Assert.Equal(["SFX", "Cartoon"], ProjectJson.Deserialize(json).FindAsset("boing")!.Tags);
        Assert.Empty(ProjectValidator.Validate(project));

        project.FindAsset("boing")!.Tags = ["SFX", "sfx", " Music"];
        Assert.Equal(["asset 'boing': tag ' Music' has spaces around it or a run of spaces"], ProjectValidator.Validate(project));
        project.FindAsset("boing")!.Tags = ["SFX", "sfx"];
        Assert.Equal(["asset 'boing' has tag 'SFX' twice"], ProjectValidator.Validate(project));
        project.FindAsset("boing")!.Tags = [""];
        Assert.Equal(["asset 'boing': a tag can't be empty"], ProjectValidator.Validate(project));
    }
}
