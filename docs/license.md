# License

ZeyoCut is free and open source, released under the MIT License. You can use it, change it and share it, for any purpose, as long as the copyright notice and the license go with copies of it.

## What you make is yours

The videos, images and other content you make with ZeyoCut belong to you. The MIT License covers ZeyoCut's code, not what you make with it.

## The MIT License

```text
MIT License

Copyright (c) 2026 Zeyo

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

## Software ZeyoCut is built from

ZeyoCut includes the .NET runtime and open-source libraries under their own licenses: MIT, BSD-3-Clause and Apache-2.0. One part isn't open source: Microsoft's DirectML, which runs AI models on a GPU, comes under Microsoft's license terms, which allow it to be redistributed with an application. [THIRD_PARTY_NOTICES.md](https://github.com/zeyarwinthu/zeyocut/blob/main/THIRD_PARTY_NOTICES.md) lists every package with its version and license, and the release zip has a `licenses` folder with the full notices of the native libraries inside the exe.

## AI models

No AI model is part of ZeyoCut. Each is downloaded from its source the first time a feature needs it, and has its own license:

| Model | Used for | License |
|---|---|---|
| BiRefNet lite | Removing the background of a picture | MIT |
| Robust Video Matting | Removing the background of video | GPL-3.0, so it stays a separate download |
| YuNet | Finding faces | MIT |
| open-image-models YOLOv9-t | Finding license plates | The code is MIT, but the license of the weights isn't stated. Check it before using plate detection commercially |

## FFmpeg and whisper.cpp

ZeyoCut doesn't include FFmpeg or whisper.cpp: you install them, and ZeyoCut runs them as separate programs. FFmpeg is LGPL or GPL depending on how it was built, and whisper.cpp is MIT.
