// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Text;

public sealed class TextMeasurer : ITextMeasurer
{
    private readonly FrameRenderer _renderer;
    private readonly Project _project;

    public TextMeasurer(FrameRenderer renderer, Project project)
    {
        _renderer = renderer;
        _project = project;
    }

    public IReadOnlyList<string> LastWarnings { get; private set; } = [];

    public (double Width, double Height) Measure(TextLayer layer)
    {
        var layout = _renderer.Text.Layout(_project, _renderer.ProjectDirectory, layer);
        LastWarnings = layout.Warnings;
        return (layout.BoxWidth, layout.BoxHeight);
    }

    public string MissingGlyphs(TextLayer layer) => _renderer.Text.Layout(_project, _renderer.ProjectDirectory, layer).Missing;
}
