// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Mcp;

// preview_server's page: the open project with a timeline scrubber and its layers, playing low-res
// frames with the mix as exported. It listens on 127.0.0.1 at a random port under a random token;
// only GET and HEAD under the token and addressed to 127.0.0.1:<port> are answered, and nothing
// allows other origins, so other sites in the browser can't read it. Nothing on it changes the
// project: the page asks for the state every second and redraws when it changes. Frames are kept
// in memory by their FrameKeys key, so an edit only redraws the frames it changes, and while the
// page plays the frames ahead are drawn in the background. The mix is made in the background into
// cache\preview\mix-<key>.m4a.
public sealed class PreviewServer : IDisposable
{
    // The longest side of a preview frame, and about how many frames a second playback shows.
    public const int FrameSize = 640;
    public const int PlaybackFps = 15;
    public const long FrameBudget = 192L * 1024 * 1024;
    private const int JpegQuality = 80;
    private const double AheadSeconds = 5;

    private static readonly string PageSecurity =
        $"default-src 'none'; script-src '{Sha256(PreviewPage.Script)}'; style-src '{Sha256(PreviewPage.Style)}'; img-src 'self' blob: data:; media-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly byte[] Page = Encoding.UTF8.GetBytes(PreviewPage.Html);

    private readonly EditorState _state;
    private readonly object _gate = new();
    private readonly object _renderGate = new();
    private readonly object _viewGate = new();
    private readonly object _audioGate = new();
    private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioWork> _audio = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _warnings = new(StringComparer.Ordinal);
    private readonly AutoResetEvent _wake = new(false);
    private HttpListener? _listener;
    private CancellationTokenSource? _stop;
    private string? _url;
    private string? _host;
    private string? _root;
    private PreviewCache _frames = new(FrameBudget);
    private FrameRenderer? _renderer;
    private int _lastFrame = -1;
    private volatile Ahead? _ahead;

    public PreviewServer(EditorState state)
    {
        _state = state;
    }

    // The page's address while it runs.
    public string? Url
    {
        get
        {
            lock (_gate)
            {
                return _url;
            }
        }
    }

    // Preview frames fit in FrameSize x FrameSize (never larger than the project), and playback shows
    // every Step-th frame.
    public static (int Width, int Height, int Step) Shape(Project project)
    {
        var scale = Math.Min(1.0, FrameSize / (double)Math.Max(project.Width, project.Height));
        return (Math.Max(1, (int)Math.Round(project.Width * scale)), Math.Max(1, (int)Math.Round(project.Height * scale)), Math.Max(1, project.Fps / PlaybackFps));
    }

    // Starts the page; false with its address when it is already running.
    public bool Start(out string url)
    {
        lock (_gate)
        {
            if (_url is not null)
            {
                url = _url;
                return false;
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            HttpListener listener;
            for (var attempt = 1; ; attempt++)
            {
                var port = FreePort();
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
                try
                {
                    listener.Start();
                    _host = $"127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException ex)
                {
                    listener.Close();
                    if (attempt == 5)
                    {
                        throw new ZeyoCutException($"could not start the preview page on 127.0.0.1: {ex.Message}");
                    }
                }
            }

            _listener = listener;
            _root = $"/{token}/";
            _url = $"http://{_host}{_root}";
            _frames = new PreviewCache(FrameBudget);
            _stop = new CancellationTokenSource();
            var stop = _stop.Token;
            _ = Task.Run(() => Serve(listener, stop), CancellationToken.None);
            new Thread(() => ReadAhead(stop)) { IsBackground = true, Name = "preview read-ahead" }.Start();
            url = _url;
            return true;
        }
    }

    // Stops the page; the address it had, or null when it wasn't running.
    public string? Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? stop;
        string? url;
        lock (_gate)
        {
            (listener, stop, url) = (_listener, _stop, _url);
            (_listener, _stop, _url, _host, _root) = (null, null, null, null, null);
        }

        if (url is null)
        {
            return null;
        }

        stop!.Cancel();
        _wake.Set();
        try
        {
            listener!.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_audioGate)
        {
            foreach (var work in _audio.Values)
            {
                work.Cancel.Cancel();
            }

            _audio.Clear();
        }

        lock (_renderGate)
        {
            _renderer?.Dispose();
            _renderer = null;
            _lastFrame = -1;
            _ahead = null;
            _frames.Dispose();
        }

        lock (_viewGate)
        {
            _views.Clear();
            _warnings.Clear();
        }

        return url;
    }

    public void Dispose() => Stop();

    // A single "bytes=a-b", "bytes=a-" or "bytes=-n": true with the bytes it asks for, false when
    // it starts past the end; null when the header isn't one such range (the whole file is sent).
    public static bool? Range(string? header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            return null;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
            {
                return null;
            }

            if (suffix == 0 || length == 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            return true;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var from))
        {
            return null;
        }

        var to = length - 1;
        if (last.Length > 0 && (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from))
        {
            return null;
        }

        if (from >= length)
        {
            return false;
        }

        (start, end) = (from, Math.Min(to, length - 1));
        return true;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task Serve(HttpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context, stop), CancellationToken.None);
        }
    }

    private void Handle(HttpListenerContext context, CancellationToken stop)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Frame-Options"] = "DENY";
            string? host;
            string? root;
            lock (_gate)
            {
                (host, root) = (_host, _root);
            }

            var path = request.Url?.AbsolutePath ?? "";
            if (root is null || stop.IsCancellationRequested)
            {
                Text(response, 503, "the preview page has stopped");
                return;
            }

            if (!string.Equals(request.Headers["Host"], host, StringComparison.OrdinalIgnoreCase))
            {
                Text(response, 403, $"the preview page only answers at {host}");
                return;
            }

            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                if (path == root[..^1])
                {
                    response.Redirect(root);
                    return;
                }

                Text(response, 404, "not found");
                return;
            }

            if (request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.Headers["Allow"] = "GET, HEAD";
                Text(response, 405, "the preview page only shows the project; edit it with ZeyoCut's tools");
                return;
            }

            var head = request.HttpMethod == "HEAD";
            var language = request.QueryString["language"];
            if (!string.IsNullOrWhiteSpace(language))
            {
                if (!Languages.TryNormalize(language, out var code))
                {
                    Text(response, 400, $"'{language}' isn't a language code", head);
                    return;
                }

                language = code;
            }
            else
            {
                language = null;
            }

            switch (path[root.Length..])
            {
                case "":
                    response.Headers["Content-Security-Policy"] = PageSecurity;
                    Send(response, 200, "text/html; charset=utf-8", Page, head);
                    break;
                case "state":
                    Send(response, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(State(language).ToJsonString()), head);
                    break;
                case "frame":
                    SendFrame(request, response, language, head, stop);
                    break;
                case "audio":
                    SendAudio(request, response, head);
                    break;
                default:
                    Text(response, 404, "not found", head);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Try(() => Text(response, 503, "the preview page has stopped"));
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Try(() => Text(response, 500, ex.Message));
        }
        finally
        {
            Try(response.Close);
        }
    }

    private JsonObject State(string? language)
    {
        var view = CurrentView(language);
        if (view is null)
        {
            return new JsonObject { ["open"] = false };
        }

        var project = view.Project;
        List<string> warnings;
        lock (_viewGate)
        {
            warnings = [.. _warnings];
        }

        return new JsonObject
        {
            ["open"] = true,
            ["revision"] = view.Revision,
            ["name"] = project.Name,
            ["width"] = project.Width,
            ["height"] = project.Height,
            ["fps"] = project.Fps,
            ["frames"] = FrameMath.FrameCount(project),
            ["duration"] = FrameMath.RenderedDuration(project),
            ["preview"] = new JsonObject { ["width"] = view.Width, ["height"] = view.Height, ["step"] = view.Step },
            ["languages"] = new JsonArray([.. view.Languages.Select(l => JsonValue.Create(l))]),
            ["language"] = view.Language,
            ["layers"] = Layers(project),
            ["markers"] = new JsonArray([.. project.Markers.OrderBy(m => m.Time).Select(m => new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["time"] = m.Time,
                ["kind"] = m.Kind == MarkerKind.Chapter ? "chapter" : "scene",
            })]),
            ["audio"] = Audio(view),
            ["warnings"] = new JsonArray([.. warnings.Select(w => JsonValue.Create(w))]),
        };
    }

    // The layers as list_layers shows them: each followed by its children, with its depth. A child's
    // time is cut to its parent's, since it only shows while its parent does (and not when it is hidden).
    private static JsonArray Layers(Project project)
    {
        var rows = new JsonArray();
        var ids = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(Layer layer, int depth, double from, double to, bool parentShown)
        {
            if (!seen.Add(layer.Id))
            {
                return;
            }

            var start = Math.Max(from, layer.Start);
            var end = Math.Max(start, Math.Min(to, SceneEvaluator.EndOf(layer, project.Duration)));
            rows.Add(new JsonObject
            {
                ["id"] = layer.Id,
                ["name"] = layer.Name == layer.Id ? null : layer.Name,
                ["type"] = layer.TypeName,
                ["start"] = start,
                ["end"] = end,
                ["depth"] = depth,
                ["visible"] = layer.Visible,
                ["shown"] = layer.Visible && parentShown,
                ["language"] = layer.Language,
                ["text"] = layer is TextLayer text ? text.Text : null,
            });
            foreach (var child in project.Layers.Where(l => l.ParentId == layer.Id))
            {
                Add(child, depth + 1, start, end, layer.Visible && parentShown);
            }
        }

        foreach (var root in project.Layers.Where(l => l.ParentId is null || !ids.Contains(l.ParentId)))
        {
            Add(root, 0, double.NegativeInfinity, double.PositiveInfinity, true);
        }

        foreach (var rest in project.Layers.Where(l => !seen.Contains(l.Id)))
        {
            Add(rest, 0, double.NegativeInfinity, double.PositiveInfinity, true);
        }

        return rows;
    }

    // The project as it is now, in the language asked for (null: every layer); null when none is open.
    private View? CurrentView(string? language)
    {
        ProjectSession session;
        try
        {
            session = _state.Session;
        }
        catch (McpException)
        {
            return null;
        }

        var json = session.Read(ProjectJson.Serialize);
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.Directory + "\n" + json)))[..16].ToLowerInvariant();
        lock (_viewGate)
        {
            var name = language ?? "";
            if (_views.TryGetValue(name, out var known) && known.Revision == revision)
            {
                return known;
            }

            if (_views.Values.Any(v => v.Revision != revision))
            {
                _views.Clear();
                _warnings.Clear();
            }

            var project = ProjectJson.Deserialize(json);
            var used = Languages.Used(project);
            Languages.Choose(project, language, export: false);
            var (width, height, step) = Shape(project);
            var view = new View(revision, language, session.Directory, json, project, used, new FrameKeys(project, session.Directory), width, height, step, PreviewAudio.Key(project, session.Directory));
            _views[name] = view;
            return view;
        }
    }

    private void SendFrame(HttpListenerRequest request, HttpListenerResponse response, string? language, bool head, CancellationToken stop)
    {
        if (!int.TryParse(request.QueryString["n"], NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
        {
            Text(response, 400, "n must be a frame number", head);
            return;
        }

        var view = CurrentView(language);
        if (view is null)
        {
            Text(response, 404, "no project is open", head);
            return;
        }

        frame = Math.Min(frame, FrameMath.FrameCount(view.Project) - 1);
        var jpeg = Frame(view, frame, stop);
        if (request.QueryString["play"] == "1")
        {
            _ahead = new Ahead(view, frame);
            _wake.Set();
        }

        Send(response, 200, "image/jpeg", jpeg, head);
    }

    private byte[] Frame(View view, int frame, CancellationToken stop)
    {
        lock (_renderGate)
        {
            stop.ThrowIfCancellationRequested();
            var key = Key(view, frame);
            if (_frames.Get<byte[]>(key) is { } cached)
            {
                return cached;
            }

            if (_renderer is null || !string.Equals(_renderer.ProjectDirectory, Path.GetFullPath(view.Directory), StringComparison.OrdinalIgnoreCase))
            {
                _renderer?.Dispose();
                _renderer = new FrameRenderer(view.Directory, () => _state.RequireFfmpeg().Ffmpeg, streamVideo: true);
                _lastFrame = -1;
            }

            // Each clip keeps one decoder going forward; a jump of more than a second ahead starts
            // new ones there instead of decoding every frame in between.
            if (_lastFrame >= 0 && frame > _lastFrame + view.Project.Fps)
            {
                _renderer.Video.CloseReaders();
            }

            _lastFrame = frame;
            using var full = _renderer.RenderImage(view.Project, frame, new RenderOptions(Proxies: true));
            using var small = Png.Scale(full, view.Width, view.Height);
            using var data = small.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new ZeyoCutException("could not encode a preview frame");
            var jpeg = data.ToArray();
            var warnings = _renderer.TakeWarnings();
            if (warnings.Count > 0)
            {
                lock (_viewGate)
                {
                    _warnings.UnionWith(warnings);
                }
            }

            return _frames.Add(key, jpeg, jpeg.Length);
        }
    }

    private static string Key(View view, int frame) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{view.Keys.Of(frame)}|{view.Width}x{view.Height}")));

    // While the page plays, draws the frames up to AheadSeconds past the one it showed last, one
    // playback step apart.
    private void ReadAhead(CancellationToken stop)
    {
        View? view = null;
        var next = 0;
        while (!stop.IsCancellationRequested)
        {
            var ahead = _ahead;
            if (ahead is null)
            {
                WaitHandle.WaitAny([_wake, stop.WaitHandle]);
                continue;
            }

            var step = ahead.View.Step;
            var last = Math.Min(FrameMath.FrameCount(ahead.View.Project) - 1, ahead.From + (int)(AheadSeconds * ahead.View.Project.Fps));
            if (!ReferenceEquals(view, ahead.View) || next <= ahead.From || next > last + step)
            {
                view = ahead.View;
                next = (ahead.From / step + 1) * step;
            }

            if (next > last)
            {
                WaitHandle.WaitAny([_wake, stop.WaitHandle]);
                continue;
            }

            try
            {
                Frame(view, next, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                _ahead = null;
            }

            next += step;
        }
    }

    private JsonObject Audio(View view)
    {
        if (view.AudioKey is not { } key)
        {
            return new JsonObject { ["status"] = "none" };
        }

        AudioWork work;
        lock (_audioGate)
        {
            var name = view.Language ?? "";
            if (!_audio.TryGetValue(name, out var known) || known.Key != key || known.Directory != view.Directory)
            {
                known?.Cancel.Cancel();
                known = StartAudio(view, key);
                _audio[name] = known;
            }

            work = known;
        }

        return new JsonObject { ["status"] = work.Status, ["key"] = key, ["message"] = work.Message };
    }

    private AudioWork StartAudio(View view, string key)
    {
        var work = new AudioWork(key, view.Directory, PreviewAudio.PathOf(view.Directory, key));
        if (File.Exists(work.Path))
        {
            work.Status = "ready";
            return work;
        }

        var cancel = work.Cancel.Token;
        var (json, language) = (view.Json, view.Language);
        _ = Task.Run(() =>
        {
            try
            {
                var project = ProjectJson.Deserialize(json);
                Languages.Choose(project, language, export: false);
                PreviewAudio.Make(project, work.Directory, _state.RequireFfmpeg(), work.Path, cancel);
                work.Status = "ready";
                DropOldMixes(work);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                work.Message = ex.Message;
                work.Status = "failed";
            }
        }, CancellationToken.None);
        return work;
    }

    // Deletes the mixes no page is playing, keeping those still being made.
    private void DropOldMixes(AudioWork done)
    {
        HashSet<string> keep;
        lock (_audioGate)
        {
            keep = _audio.Values.Select(w => w.Path).Append(done.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var folder = Path.GetDirectoryName(done.Path)!;
        foreach (var file in Directory.EnumerateFiles(folder, "mix-*.m4a"))
        {
            if (!keep.Contains(file) && !file.EndsWith(".partial.m4a", StringComparison.OrdinalIgnoreCase))
            {
                Try(() => File.Delete(file));
            }
        }
    }

    private void SendAudio(HttpListenerRequest request, HttpListenerResponse response, bool head)
    {
        var key = request.QueryString["k"];
        string? path;
        lock (_audioGate)
        {
            path = _audio.Values.FirstOrDefault(w => w.Key == key && w.Status == "ready")?.Path;
        }

        if (path is null || !File.Exists(path))
        {
            Text(response, 404, "that mix isn't ready", head);
            return;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        var range = Range(request.Headers["Range"], length, out var start, out var end);
        if (range == false)
        {
            response.Headers["Content-Range"] = $"bytes */{length}";
            Text(response, 416, "the range starts past the end of the mix", head);
            return;
        }

        if (range == true)
        {
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }

        response.ContentType = "audio/mp4";
        response.ContentLength64 = end - start + 1;
        if (head)
        {
            return;
        }

        file.Position = start;
        var buffer = new byte[81920];
        for (var left = end - start + 1; left > 0;)
        {
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0)
            {
                break;
            }

            response.OutputStream.Write(buffer, 0, read);
            left -= read;
        }
    }

    private static void Send(HttpListenerResponse response, int status, string type, byte[] body, bool head)
    {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        if (!head)
        {
            response.OutputStream.Write(body);
        }
    }

    private static void Text(HttpListenerResponse response, int status, string message, bool head = false) =>
        Send(response, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message), head);

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }

    private static string Sha256(string text) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The project at one revision in one language, and the keys of its frames.
    private sealed record View(
        string Revision,
        string? Language,
        string Directory,
        string Json,
        Project Project,
        IReadOnlyList<string> Languages,
        FrameKeys Keys,
        int Width,
        int Height,
        int Step,
        string? AudioKey);

    private sealed record Ahead(View View, int From);

    private sealed class AudioWork(string key, string directory, string path)
    {
        public string Key { get; } = key;

        public string Directory { get; } = directory;

        public string Path { get; } = path;

        public CancellationTokenSource Cancel { get; } = new();

        public volatile string Status = "making";

        public volatile string? Message;
    }
}
