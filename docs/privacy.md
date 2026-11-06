# Privacy

ZeyoCut runs on your computer. This page says what it does with your data, what your AI client does with it, and what this website collects. It was last changed on 5 November 2026.

## ZeyoCut

- **No telemetry, no account.** ZeyoCut doesn't collect usage data, crash reports or analytics, and you don't sign in to anything. The telemetry of ONNX Runtime, the library that runs the AI models, is turned off.
- **Your files stay on your computer.** Projects, the library, logs and AI models are kept in your user folders (see [Folders](install.md#folders)). ZeyoCut reads files only from the folders it is allowed to use.
- **One kind of download.** The first time a feature needs an AI model (background removal, or face and plate detection), ZeyoCut downloads it from its original source on `github.com` or `huggingface.co` and checks it against a pinned SHA-256. Like any download, that site sees your IP address and which file was requested. Nothing about your projects is sent.
- **AI on your computer.** Background removal and face and plate detection run on your own CPU or GPU. Detection finds where faces and plates are, to hide them; it doesn't recognize anyone or read any plate.
- **DirectML.** To run models on a GPU, ZeyoCut uses DirectML, a Microsoft library. Its license says it may send diagnostic information to Microsoft, as your Windows diagnostic data settings allow. With the `AiDevice` setting `cpu`, every model runs on the CPU instead.
- **The preview page** runs on `127.0.0.1`, so only your own computer can open it.
- **No outside services.** The optional `describe_clip` tool would send a short stretch of video to a video-understanding service that you set up with your own API key. No such service is built in yet, so the tool doesn't appear.

## Your AI client

You use ZeyoCut through an MCP client such as Claude Desktop or Claude Code. The client sends your prompts and ZeyoCut's replies to the AI model it uses. Those replies can include file and folder names, transcripts, captions, project details and the rendered frames the model looks at to check its work. How that data is handled is decided by your client and AI provider; see their privacy policies.

## This website

`cut.zeyoapp.com` is a static site hosted on GitHub Pages. It has no cookies, analytics or trackers, and loads no fonts or scripts from other sites. Search runs in your browser, and your choice of light or dark mode is remembered in your browser's local storage. GitHub, which serves the site, may log visitors' IP addresses for security; see the [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Questions

Please open an issue on [GitHub](https://github.com/zeyarwinthu/zeyocut/issues).
