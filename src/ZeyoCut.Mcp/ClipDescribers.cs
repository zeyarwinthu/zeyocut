// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using ZeyoCut.Mcp.Tools;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Mcp;

// Picks the describe_clip provider from the settings. No provider is built in yet. To add one (Gemini
// is planned), implement IClipDescriber in ZeyoCut.Render/Video and list it in Providers under its
// config name. describe_clip is on the server only while a provider loads.
public static class ClipDescribers
{
    // Config name → the provider, made from its API key and the settings.
    private static readonly Dictionary<string, Func<string, Settings, IClipDescriber>> Providers = new(StringComparer.OrdinalIgnoreCase);

    public static IClipDescriber? Create(Settings settings, out string status) =>
        Create(settings, Environment.GetEnvironmentVariable, Providers, out status);

    // Null when describe_clip stays off; status says why, or which provider is on. The API key is
    // only ever read from the environment variable the settings name, never from a file.
    public static IClipDescriber? Create(
        Settings settings,
        Func<string, string?> environment,
        IReadOnlyDictionary<string, Func<string, Settings, IClipDescriber>> providers,
        out string status)
    {
        var name = settings.DescriberProvider?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            status = $"describe_clip is off: no clip describer is configured (ZeyoCut:ClipDescriber:Provider in appsettings.json, or {Settings.DescriberProviderVariable})";
            return null;
        }

        if (!providers.TryGetValue(name, out var create))
        {
            var available = providers.Count == 0 ? "none yet" : string.Join(", ", providers.Keys.Order(StringComparer.OrdinalIgnoreCase));
            status = $"describe_clip is off: there is no clip describer named '{name}' in this build (available: {available})";
            return null;
        }

        var variable = settings.DescriberKeyVariable;
        var key = environment(variable)?.Trim().Trim('"');
        if (string.IsNullOrEmpty(key))
        {
            status = $"describe_clip is off: the {name} clip describer needs its API key in the environment variable {variable}";
            return null;
        }

        var describer = create(key, settings);
        status = $"describe_clip is on: clip describer {describer.Provider} (API key from {variable})";
        return describer;
    }

    // Adds describe_clip, backed by describer. Program calls this only when a provider loaded.
    public static IMcpServerBuilder WithClipDescriber(this IMcpServerBuilder builder, IClipDescriber describer)
    {
        builder.Services.AddSingleton(describer);

        // Typed as IEnumerable<Type>: an array would bind to WithTools<T>(T target) and add nothing.
        IEnumerable<Type> types = [typeof(ClipTools)];
        return builder.WithTools(types);
    }
}
