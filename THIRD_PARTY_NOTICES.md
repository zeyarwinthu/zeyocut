# Third-party notices

ZeyoCut is licensed under the MIT License (see [LICENSE](LICENSE)). The videos, images and other
content you make with ZeyoCut are yours; this file is only about the software ZeyoCut is built from.

What a ZeyoCut release contains, and what it doesn't:

- **In `ZeyoCut.Mcp.exe`:** the .NET runtime and the NuGet packages below, with the native libraries
  they carry (Skia, HarfBuzz, OpenCV, ONNX Runtime and DirectML).
- **Downloaded on first use, never bundled:** the AI models, each from its own source (see
  [AI models](#ai-models-downloaded-on-first-use)).
- **Installed by you, never bundled:** FFmpeg and, for transcripts, whisper.cpp. ZeyoCut runs them as
  separate programs (see [Programs you install](#programs-you-install)).

The release zip also has a `licenses` folder with the full notice files of the bundled native code:
the .NET runtime's, SkiaSharp's (Skia, HarfBuzz and the libraries they use), ONNX Runtime's, and
DirectML's license and notices.

## .NET runtime

ZeyoCut.Mcp.exe is self-contained: it includes the .NET 10 runtime, MIT License, Copyright (c)
.NET Foundation and Contributors ([github.com/dotnet/runtime](https://github.com/dotnet/runtime)).
Its third-party notices are `licenses/dotnet-THIRD-PARTY-NOTICES.txt` in the release zip.

## NuGet packages in the release

| Package | Version | License | Copyright | Source |
|---|---|---|---|---|
| HarfBuzzSharp | 14.2.1.400 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| HarfBuzzSharp.NativeAssets.macOS (macOS files only; not in the Windows release) | 14.2.1.400 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| HarfBuzzSharp.NativeAssets.Win32 | 14.2.1.400 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| Microsoft.AI.DirectML | 1.15.4 | Microsoft Software License Terms for DirectML (not open source; see below) | © Microsoft Corporation. All rights reserved. | [aka.ms/DirectML](https://aka.ms/DirectML) |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Caching.Abstractions | 10.0.13 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.Binder | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.Json | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Diagnostics | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.FileProviders.Physical | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Hosting | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.Configuration | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.Console | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.Debug | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.EventLog | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Logging.EventSource | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Options | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Extensions.Primitives | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/Microsoft/onnxruntime](https://github.com/Microsoft/onnxruntime) |
| Microsoft.ML.OnnxRuntime.Managed | 1.24.4 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/Microsoft/onnxruntime](https://github.com/Microsoft/onnxruntime) |
| ModelContextProtocol | 2.3.1 | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. | [csharp.sdk.modelcontextprotocol.io](https://csharp.sdk.modelcontextprotocol.io/) |
| ModelContextProtocol.Core | 2.3.1 | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. | [csharp.sdk.modelcontextprotocol.io](https://csharp.sdk.modelcontextprotocol.io/) |
| OpenCvSharp4 | 4.13.0.20260627 | Apache-2.0 | Copyright 2008-2026 | [github.com/shimat/opencvsharp](https://github.com/shimat/opencvsharp) |
| OpenCvSharp4.runtime.win | 4.13.0.20260627 | Apache-2.0 | Copyright 2008-2026 | [github.com/shimat/opencvsharp](https://github.com/shimat/opencvsharp) |
| SkiaSharp | 4.154.0 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.HarfBuzz | 4.154.0 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.NativeAssets.macOS (macOS files only; not in the Windows release) | 4.154.0 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.NativeAssets.Win32 | 4.154.0 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) |
| System.Diagnostics.EventLog | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| System.Numerics.Tensors | 9.0.0 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |

### Native libraries inside these packages

- **Skia** (SkiaSharp.NativeAssets.Win32): BSD-3-Clause, Copyright Google LLC and the Skia authors.
  **HarfBuzz** (HarfBuzzSharp.NativeAssets.Win32): the "Old MIT" license. Both, with the libraries
  they use, are in `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`.
- **OpenCV** (OpenCvSharp4.runtime.win): Apache-2.0
  ([github.com/opencv/opencv](https://github.com/opencv/opencv)), with the third-party libraries
  OpenCV builds in under their own licenses (listed in OpenCV's `3rdparty` folder). The release leaves
  out OpenCV's FFmpeg plugin (`opencv_videoio_ffmpeg`).
- **ONNX Runtime** (Microsoft.ML.OnnxRuntime.DirectML): MIT. Its third-party notices are
  `licenses/onnxruntime-ThirdPartyNotices.txt`. ONNX Runtime's Windows builds can report usage events
  through Windows' own telemetry; ZeyoCut turns that off before it loads a model.
- **DirectML** (Microsoft.AI.DirectML, `DirectML.dll`): Microsoft's own license terms, not an
  open-source license. They allow it to be redistributed as part of an application, for use on Windows,
  and they don't allow reverse engineering it. The terms also say DirectML may collect information and
  send it to Microsoft, as Windows' diagnostic data settings allow. ZeyoCut uses DirectML only to run
  AI models on the GPU; the full terms are `licenses/DirectML-LICENSE.txt` and its notices
  `licenses/DirectML-ThirdPartyNotices.txt`.

## AI models (downloaded on first use)

ZeyoCut doesn't include any model. The first time a feature needs one, it downloads the model from
the URL below (pinned to one version), checks its size and SHA-256 (in
[src/ZeyoCut.Render/AiModels.cs](src/ZeyoCut.Render/AiModels.cs)) and only then uses it. The tool's
reply names the model and its license before the download starts.

| Model | Used by | File | License | Source |
|---|---|---|---|---|
| BiRefNet lite | `remove_background` (image method) | birefnet_lite.onnx (224 MB) | MIT | [huggingface.co/onnx-community/BiRefNet_lite-ONNX](https://huggingface.co/onnx-community/BiRefNet_lite-ONNX), from [github.com/ZhengPeng7/BiRefNet](https://github.com/ZhengPeng7/BiRefNet) |
| Robust Video Matting (MobileNetV3) | `remove_background` (video method) | rvm_mobilenetv3_fp32.onnx (15 MB) | GPL-3.0 | [github.com/PeterL1n/RobustVideoMatting](https://github.com/PeterL1n/RobustVideoMatting) |
| YuNet | `detect_and_track` (faces) | face_detection_yunet_2026may.onnx (230 KB) | MIT | [github.com/opencv/opencv_zoo](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) |
| open-image-models YOLOv9-t | `detect_and_track` (license plates) | yolo-v9-t-640-license-plates-end2end.onnx (8 MB) | code MIT; the weights' license isn't stated | [github.com/ankandrew/open-image-models](https://github.com/ankandrew/open-image-models) |

- Robust Video Matting is GPL-3.0, so it stays a separate download: ZeyoCut's code and releases never
  contain it, and ZeyoCut runs it only as a model file.
- The license-plate model's repository is MIT, but the weights' license and training data aren't
  stated. Check them yourself before using plate detection commercially.

## Programs you install

- **FFmpeg** ([ffmpeg.org](https://ffmpeg.org/)) is LGPL-2.1-or-later or GPL-2.0-or-later, depending on
  how it was built (the gyan.dev builds that `winget install Gyan.FFmpeg` gives are GPL-3.0). ZeyoCut
  doesn't include FFmpeg: it finds `ffmpeg.exe` and `ffprobe.exe` on your computer and runs them as
  separate programs.
- **whisper.cpp** ([github.com/ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp)) is MIT, and
  its `ggml-*.bin` models are converted from OpenAI's Whisper (MIT). ZeyoCut runs `whisper-cli.exe`
  as a separate program when you set it up for transcripts.

## Used only to build and test (not in the release)

| Package | Version | License | Copyright | Source |
|---|---|---|---|---|
| Microsoft.ApplicationInsights | 2.23.0 | MIT | © Microsoft Corporation. All rights reserved. | [go.microsoft.com/fwlink/?LinkId=392727](https://go.microsoft.com/fwlink/?LinkId=392727) |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| Microsoft.Testing.Extensions.Telemetry | 2.5.1 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/microsoft/testfx](https://github.com/microsoft/testfx) |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/microsoft/testfx](https://github.com/microsoft/testfx) |
| Microsoft.Testing.Platform | 2.5.1 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/microsoft/testfx](https://github.com/microsoft/testfx) |
| Microsoft.Testing.Platform.MSBuild | 2.5.1 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/microsoft/testfx](https://github.com/microsoft/testfx) |
| Microsoft.Win32.Registry | 5.0.0 | MIT | © Microsoft Corporation. All rights reserved. | [github.com/dotnet/runtime](https://github.com/dotnet/runtime) |
| System.Security.AccessControl | 6.0.1 | MIT | © Microsoft Corporation. All rights reserved. | [dot.net](https://dot.net/) |
| xunit.analyzers | 2.2.0 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit.analyzers](https://github.com/xunit/xunit.analyzers) |
| xunit.v3 | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.assert | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.common | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.core.mtp-v2 | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.extensibility.core | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.mtp-v2 | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.runner.common | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |
| xunit.v3.runner.inproc.console | 4.0.2 | Apache-2.0 | Copyright (C) .NET Foundation | [github.com/xunit/xunit](https://github.com/xunit/xunit) |

The face and plate detection tests use three photos from Wikimedia Commons: two NASA photos, which are in
the public domain, and one dedicated to the public domain (CC0 1.0). Their sources are in
[tests/ZeyoCut.Tests/Fixtures/README.md](tests/ZeyoCut.Tests/Fixtures/README.md).

## License texts

### MIT License

Each MIT-licensed package above is under this license, with the copyright line given in its row.

```text
MIT License

Copyright (c) <the copyright holder named in the package's row>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

ModelContextProtocol, ModelContextProtocol.Core, OpenCvSharp4, OpenCvSharp4.runtime.win, OpenCV and
the xunit packages are under this license.

```text
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "{}"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright {yyyy} {name of copyright owner}

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
