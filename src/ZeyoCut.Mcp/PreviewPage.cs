// SPDX-License-Identifier: MIT

namespace ZeyoCut.Mcp;

// The preview page (PreviewServer): one HTML page with its style and script inline and nothing
// loaded from elsewhere. The Content-Security-Policy allows only this style and script, by hash.
// Text from the project is only ever set as text, never as markup.
public static class PreviewPage
{
    public const string Style = """
        :root { color-scheme: dark; --bg: #141417; --panel: #1d1d22; --line: #2c2c33; --text: #e8e8ec; --muted: #9a9aa6; --accent: #ff5a5f; --label: 300px; }
        * { box-sizing: border-box; }
        [hidden] { display: none !important; }
        body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.4 "Segoe UI", system-ui, sans-serif; }
        header { display: flex; align-items: baseline; gap: 12px; padding: 10px 16px; border-bottom: 1px solid var(--line); flex-wrap: wrap; }
        h1 { font-size: 16px; margin: 0; font-weight: 600; }
        #info, #readonly, #frameNo { color: var(--muted); }
        #readonly { margin-left: auto; font-size: 12px; }
        main { padding: 12px 16px 24px; }
        #stage { position: relative; display: flex; justify-content: center; background: #000; border-radius: 6px; padding: 8px; min-height: 160px; }
        #screen { display: block; max-width: 100%; max-height: 62vh; width: auto; height: auto; background: #000; }
        #message { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center; padding: 24px; border-radius: 6px; background: rgba(20, 20, 23, 0.9); }
        #controls { display: flex; align-items: center; gap: 12px; margin: 12px 0 8px; flex-wrap: wrap; }
        #time, #frameNo { font-variant-numeric: tabular-nums; }
        button, select { background: var(--panel); color: var(--text); border: 1px solid var(--line); border-radius: 6px; font: inherit; }
        button { padding: 6px 16px; min-width: 76px; cursor: pointer; }
        button:hover { border-color: #4a4a55; }
        select { padding: 4px 6px; }
        label { color: var(--muted); margin-left: auto; }
        #timeline { position: relative; background: var(--panel); border: 1px solid var(--line); border-radius: 6px; user-select: none; touch-action: none; overflow: hidden; }
        .row { display: grid; grid-template-columns: var(--label) 1fr; align-items: center; border-top: 1px solid var(--line); }
        .row:first-child { border-top: 0; }
        .row.active { background: rgba(255, 255, 255, 0.05); }
        .row.hidden { opacity: 0.45; }
        .name { padding: 3px 10px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--muted); }
        .row.active .name strong { color: #fff; }
        .name strong { color: var(--text); font-weight: 600; }
        .lane { position: relative; height: 26px; margin-right: 14px; cursor: pointer; overflow: hidden; }
        .span { position: absolute; top: 6px; height: 14px; border-radius: 3px; min-width: 2px; }
        .tick { position: absolute; top: 0; bottom: 0; border-left: 1px solid #3a3a44; padding: 5px 0 0 3px; font-size: 11px; color: var(--muted); white-space: nowrap; }
        .marker { position: absolute; top: 4px; bottom: 4px; border-left: 2px solid; padding-left: 4px; font-size: 11px; white-space: nowrap; }
        .marker.chapter { border-color: #ffd166; color: #ffd166; }
        .marker.scene { border-color: #7fc8f8; color: #7fc8f8; }
        .badge { font-size: 11px; border: 1px solid var(--line); border-radius: 4px; padding: 0 4px; margin-left: 6px; }
        .detail { margin-left: 6px; }
        .video { background: #4a8fe7; }
        .audio { background: #3fb37f; }
        .text { background: #e0a440; }
        .image { background: #a877e0; }
        .cutout { background: #e06f98; }
        .redaction { background: #8a8a95; }
        #playhead { position: absolute; top: 0; bottom: 0; width: 2px; margin-left: -1px; background: var(--accent); pointer-events: none; }
        #status { color: var(--muted); font-size: 12px; margin: 8px 0 0; }
        #warnings { color: #e7b84a; font-size: 12px; margin: 4px 0 0; padding-left: 18px; }
        @media (max-width: 720px) { :root { --label: 150px; } #readonly { margin-left: 0; } }
        """;

    public const string Script = """
        "use strict";
        const $ = id => document.getElementById(id);
        const screen = $("screen"), ctx = screen.getContext("2d"), sound = $("sound"), rows = $("rows"), timeline = $("timeline"), playhead = $("playhead");
        let state = null, view = "", frame = 0, shown = -1, loading = false, wanted = 0, wantedAhead = false;
        let playing = false, clockFrom = 0, clockStart = 0, audioKey = null, language = "";
        let ruler = null, layerRows = [], shownUi = -1, gone = false, noProject = false, frameProblem = "", warningsShown = "";

        function query(path, extra) {
          const params = new URLSearchParams(extra || {});
          if (language) params.set("language", language);
          const text = params.toString();
          return text ? path + "?" + text : path;
        }

        function stamp(t) {
          const total = Math.max(0, Math.round(t * 100));
          const two = n => String(n).padStart(2, "0");
          const cs = total % 100, s = Math.floor(total / 100) % 60, m = Math.floor(total / 6000) % 60, h = Math.floor(total / 360000);
          return (h ? h + ":" + two(m) : String(m)) + ":" + two(s) + "." + two(cs);
        }

        function notice() {
          const text = gone
            ? "The preview page isn't answering. It stops when ZeyoCut closes or when preview_server stops it; start it again with preview_server and open the address it gives."
            : noProject ? "No project is open. Once open_project or create_project opens one, it shows here." : frameProblem;
          const box = $("message");
          box.textContent = text;
          box.hidden = !text;
        }

        async function refresh() {
          try {
            const response = await fetch(query("state"), { cache: "no-store" });
            if (!response.ok) throw new Error(await response.text());
            gone = false;
            apply(await response.json());
          } catch (error) {
            if (error instanceof TypeError) {
              if (playing) pause();
              gone = true;
            }
            $("status").textContent = String(error.message || error);
          }
          notice();
        }

        async function loop() {
          await refresh();
          setTimeout(loop, 1000);
        }

        function apply(s) {
          noProject = !s.open;
          if (!s.open) {
            if (playing) pause();
            state = null;
            view = "";
            rows.replaceChildren();
            playhead.hidden = true;
            for (const id of ["info", "time", "frameNo", "status"]) $(id).textContent = "";
            $("title").textContent = "ZeyoCut preview";
            $("warnings").replaceChildren();
            warningsShown = "";
            $("languageBox").hidden = true;
            setAudio(null);
            ctx.clearRect(0, 0, screen.width, screen.height);
            document.title = "ZeyoCut preview";
            return;
          }

          state = s;
          const key = s.revision + "|" + (s.language || "");
          if (key !== view) {
            view = key;
            document.title = s.name + " · ZeyoCut preview";
            $("title").textContent = s.name;
            $("info").textContent = `${s.width}x${s.height} @ ${s.fps} fps · ${stamp(s.duration)} · ${s.layers.length} layer(s)`;
            if (screen.width !== s.preview.width || screen.height !== s.preview.height) {
              screen.width = s.preview.width;
              screen.height = s.preview.height;
            }

            frame = Math.min(frame, s.frames - 1);
            languages(s);
            build(s);
            shown = -1;
            shownUi = -1;
            frameProblem = "";
          }

          setAudio(s.audio);
          status(s);
          if (!playing) request(frame, false);
        }

        function languages(s) {
          const select = $("language");
          const codes = ["", ...s.languages];
          if (s.language && !codes.includes(s.language)) codes.push(s.language);
          $("languageBox").hidden = codes.length < 3;
          if (select.options.length !== codes.length || [...select.options].some((option, i) => option.value !== codes[i])) {
            select.replaceChildren(...codes.map(code => {
              const option = document.createElement("option");
              option.value = code;
              option.textContent = code || "every language";
              return option;
            }));
          }

          select.value = s.language || "";
        }

        function at(t) {
          return 100 * Math.min(Math.max(t / state.duration, 0), 1) + "%";
        }

        function row(kind, label) {
          const element = document.createElement("div");
          element.className = "row " + kind;
          const name = document.createElement("div");
          name.className = "name";
          name.textContent = label;
          const lane = document.createElement("div");
          lane.className = "lane";
          element.append(name, lane);
          return { element, name, lane };
        }

        function badge(parent, text) {
          const span = document.createElement("span");
          span.className = "badge";
          span.textContent = text;
          parent.append(span);
        }

        function build(s) {
          ruler = row("ruler", "time");
          const steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
          const every = steps.find(step => s.duration / step <= 12) || 7200;
          for (let i = 0; i * every < s.duration - 1e-9; i++) {
            const tick = document.createElement("div");
            tick.className = "tick";
            tick.style.left = at(i * every);
            tick.textContent = stamp(i * every).replace(/\.00$/, "");
            ruler.lane.append(tick);
          }

          const parts = [ruler.element];
          if (s.markers.length) {
            const markers = row("markers", "markers");
            for (const m of s.markers) {
              const mark = document.createElement("div");
              mark.className = "marker " + m.kind;
              mark.style.left = at(m.time);
              mark.dataset.time = m.time;
              mark.title = `${m.name} (${m.kind}, ${stamp(m.time)})`;
              mark.textContent = m.name;
              markers.lane.append(mark);
            }

            parts.push(markers.element);
          }

          layerRows = s.layers.map(layer => {
            const r = row("layer" + (layer.shown ? "" : " hidden"), layer.depth ? "└ " : "");
            r.name.style.paddingLeft = 10 + 16 * layer.depth + "px";
            const id = document.createElement("strong");
            id.textContent = layer.id;
            r.name.append(id);
            badge(r.name, layer.type);
            if (layer.language) badge(r.name, layer.language);
            if (!layer.visible) badge(r.name, "hidden");
            const detail = layer.text != null ? "“" + layer.text + "”" : layer.name;
            if (detail) {
              const span = document.createElement("span");
              span.className = "detail";
              span.textContent = detail;
              r.name.append(span);
            }

            r.name.title = [layer.id, layer.type, layer.name, layer.text].filter(Boolean).join(" · ") + ` · ${stamp(layer.start)}–${stamp(layer.end)}`;
            const start = Math.max(0, layer.start), end = Math.min(s.duration, layer.end);
            const bar = document.createElement("div");
            bar.className = "span " + layer.type;
            bar.style.left = at(start);
            bar.style.width = 100 * Math.max(0, end - start) / s.duration + "%";
            r.lane.append(bar);
            parts.push(r.element);
            return { element: r.element, layer };
          });

          rows.replaceChildren(...parts);
          playhead.hidden = false;
        }

        function status(s) {
          const a = s.audio;
          const heard = a.status === "none" ? "nothing in the project is heard"
            : a.status === "making" ? "mixing the sound as the export mixes it; it plays once it is ready"
            : a.status === "ready" ? "the sound is mixed as the export mixes it"
            : "the sound couldn't be mixed: " + a.message;
          $("status").textContent = `Frames ${s.preview.width}x${s.preview.height} at ${+(s.fps / s.preview.step).toFixed(2)} fps while playing, drawn when first shown and kept in memory · ${heard}`;
          const warnings = JSON.stringify(s.warnings);
          if (warnings !== warningsShown) {
            warningsShown = warnings;
            $("warnings").replaceChildren(...s.warnings.map(text => {
              const item = document.createElement("li");
              item.textContent = text;
              return item;
            }));
          }
        }

        function setAudio(audio) {
          const key = audio && audio.status === "ready" ? audio.key : null;
          if (key === audioKey) return;
          audioKey = key;
          if (key) {
            sound.src = query("audio", { k: key });
            if (playing) startSound(clockFrom + (performance.now() - clockStart) / 1000);
          } else {
            sound.pause();
            sound.removeAttribute("src");
            sound.load();
          }
        }

        function startSound(t) {
          if (!audioKey) return;
          try { sound.currentTime = t; } catch (error) { }
          sound.play().catch(() => { });
        }

        function play() {
          if (!state || playing) return;
          if (frame >= state.frames - 1) frame = 0;
          playing = true;
          clockFrom = frame / state.fps;
          clockStart = performance.now();
          startSound(clockFrom);
          $("play").textContent = "Pause";
        }

        function pause() {
          if (!playing) return;
          playing = false;
          sound.pause();
          $("play").textContent = "Play";
          if (state) request(frame, false);
        }

        function seek(n) {
          if (!state) return;
          frame = Math.min(Math.max(n, 0), state.frames - 1);
          if (playing) {
            clockFrom = frame / state.fps;
            clockStart = performance.now();
            startSound(clockFrom);
          } else {
            request(frame, false);
          }
        }

        function request(n, ahead) {
          wanted = n;
          wantedAhead = ahead;
          if (!loading && n !== shown) load();
        }

        async function load() {
          loading = true;
          const n = wanted, ahead = wantedAhead, from = view;
          try {
            const response = await fetch(query("frame", ahead ? { n, play: 1 } : { n }), { cache: "no-store" });
            if (!response.ok) throw new Error(await response.text());
            const bitmap = await createImageBitmap(await response.blob());
            ctx.drawImage(bitmap, 0, 0, screen.width, screen.height);
            bitmap.close();
            frameProblem = "";
          } catch (error) {
            frameProblem = error instanceof TypeError ? "" : `Frame ${n} couldn't be drawn: ${error.message || error}`;
          }

          if (from === view) shown = n;
          loading = false;
          notice();
          if (state && wanted !== shown) load();
        }

        function ui() {
          shownUi = frame;
          const t = frame / state.fps;
          $("time").textContent = stamp(t) + " / " + stamp(state.duration);
          $("frameNo").textContent = `frame ${frame} of ${state.frames}`;
          const lane = ruler.lane.getBoundingClientRect(), outer = timeline.getBoundingClientRect();
          playhead.style.left = lane.left - outer.left + lane.width * Math.min(t / state.duration, 1) + "px";
          for (const r of layerRows) r.element.classList.toggle("active", r.layer.shown && t >= r.layer.start && t < r.layer.end);
        }

        function tick() {
          if (state && playing) {
            let t;
            if (audioKey && !sound.paused && !sound.seeking && sound.readyState >= 2) {
              t = sound.currentTime;
              clockFrom = t;
              clockStart = performance.now();
            } else {
              t = clockFrom + (performance.now() - clockStart) / 1000;
            }

            if (t >= state.duration) {
              frame = state.frames - 1;
              pause();
            } else {
              frame = Math.min(state.frames - 1, Math.floor(t * state.fps + 1e-6));
              request(Math.floor(frame / state.preview.step) * state.preview.step, true);
            }
          }

          if (state && frame !== shownUi) ui();
          requestAnimationFrame(tick);
        }

        let dragging = false;
        function seekAt(x) {
          const lane = ruler.lane.getBoundingClientRect();
          seek(Math.round(Math.min(Math.max((x - lane.left) / lane.width, 0), 1) * state.duration * state.fps));
        }

        timeline.addEventListener("pointerdown", event => {
          if (!state || !event.target.closest(".lane")) return;
          const mark = event.target.closest(".marker");
          if (mark) {
            seek(Math.round(Number(mark.dataset.time) * state.fps));
            return;
          }

          dragging = true;
          timeline.setPointerCapture(event.pointerId);
          seekAt(event.clientX);
        });
        timeline.addEventListener("pointermove", event => { if (dragging && state) seekAt(event.clientX); });
        timeline.addEventListener("pointerup", () => { dragging = false; });
        timeline.addEventListener("pointercancel", () => { dragging = false; });
        $("play").addEventListener("click", event => {
          playing ? pause() : play();
          event.currentTarget.blur();
        });
        $("language").addEventListener("change", event => {
          language = event.target.value;
          refresh();
        });
        document.addEventListener("keydown", event => {
          if (!state || event.target instanceof Element && event.target.closest("select")) return;
          if (event.key === " ") {
            event.preventDefault();
            playing ? pause() : play();
          } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
            event.preventDefault();
            seek(frame + (event.shiftKey ? state.fps : 1) * (event.key === "ArrowLeft" ? -1 : 1));
          } else if (event.key === "Home") {
            event.preventDefault();
            seek(0);
          } else if (event.key === "End") {
            event.preventDefault();
            seek(state.frames - 1);
          }
        });
        window.addEventListener("resize", () => { shownUi = -1; });
        loop();
        requestAnimationFrame(tick);
        """;

    public static string Html { get; } = $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="no-referrer">
        <link rel="icon" href="data:,">
        <title>ZeyoCut preview</title>
        <style>{{Style}}</style>
        </head>
        <body>
        <header>
        <h1 id="title">ZeyoCut preview</h1>
        <span id="info"></span>
        <span id="readonly">Read-only: edits are made with ZeyoCut's tools, and this page follows them.</span>
        </header>
        <main>
        <div id="stage"><canvas id="screen" width="640" height="360"></canvas><div id="message" hidden></div></div>
        <div id="controls">
        <button id="play" type="button" title="Play or pause (space); arrow keys step a frame, with shift a second">Play</button>
        <span id="time"></span>
        <span id="frameNo"></span>
        <label id="languageBox" hidden>Language <select id="language"></select></label>
        </div>
        <div id="timeline"><div id="rows"></div><div id="playhead" hidden></div></div>
        <p id="status"></p>
        <ul id="warnings"></ul>
        </main>
        <audio id="sound" preload="auto"></audio>
        <script>{{Script}}</script>
        </body>
        </html>
        """;
}
