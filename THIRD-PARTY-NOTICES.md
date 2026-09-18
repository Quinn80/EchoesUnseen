# Third-party notices — Echoes Unseen

Echoes Unseen ships or downloads the components below. Everything here runs on the
player's own machine; nothing on this page sends anything anywhere.

---

## RapidOcrNet 4.2.0 — Apache-2.0

<https://github.com/BobLd/RapidOcrNet>

A C# port of [RapidOCR](https://github.com/RapidAI/RapidOCR) (Apache-2.0), running
PaddleOCR models through ONNX Runtime. It is the hover reader's recognition engine — it
reads the text inside whatever object OpenCV has found under the pointer — with Windows
OCR as the local fallback. It was chosen on measured results against real Guild Wars 2 captures, where it found about half again as many lines as Windows OCR with the lowest junk rate of the three engines tried.

### PP-OCRv5 models — Apache-2.0

From [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR), redistributed in the
RapidOcrNet package and embedded in the Echoes Unseen executable:

| file | purpose | size |
|---|---|---|
| `ch_PP-OCRv5_mobile_det.onnx` | text detection (DBNet) | 4.8 MB |
| `latin_PP-OCRv5_rec_mobile_infer.onnx` | Latin recognition (CRNN) | 7.9 MB |
| `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | line orientation | 1.0 MB |
| `ppocrv5_latin_dict.txt` | character dictionary | 2 KB |

These are small neural networks that turn a picture of letters into letters. They do
not generate text, make decisions, or play the game.

### Microsoft.ML.OnnxRuntime 1.29.0 (and .Managed) — MIT

<https://github.com/microsoft/onnxruntime>

Runs the PP-OCRv5 models on the CPU of the machine it is installed on.

### SkiaSharp 3.119.1 — MIT

<https://github.com/mono/SkiaSharp>

### Clipper2 2.0.0 — Boost Software License 1.0

<https://github.com/AngusJohnson/Clipper2>

Polygon clipping and offsetting, used by RapidOcrNet when it turns the text detector's
output into text boxes.

---

## OpenCvSharp 4.13.0.20260627 — Apache-2.0

<https://github.com/shimat/opencvsharp> — packages `OpenCvSharp4` and
`OpenCvSharp4.runtime.win.slim`, version 4.13.0.20260627. Both declare the licence
expression `Apache-2.0` in their package metadata.

Used **only by the hover reader**, as part of *Enhanced Hover Targeting* (on by default
since b1.5; turning it off in Settings returns to Classic Hover Targeting). It finds the
shapes of the interface — rows, cards, grid cells, buttons, tooltip borders — so the hover
reader can tell which one object is under the pointer. It reads no text: RapidOCR does all
the reading. The chat reader does not use OpenCV. If the native library cannot load, the
classic hover reader is used.

The native library `OpenCvSharpExtern.dll` (Windows x64, "slim" profile: core, imgproc,
imgcodecs, calib3d, features2d, flann, objdetect, photo) is a single file with no
separate FFmpeg, GUI or video DLLs. Its embedded build information and contents show
these components compiled into it:

| component | version | licence |
|---|---|---|
| [OpenCV](https://github.com/opencv/opencv) | 4.13.0 | Apache-2.0 |
| [zlib](https://zlib.net) | 1.3.1 | zlib |
| [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) | (libjpeg API 7.0) | IJG, BSD-3-Clause, zlib |
| [libpng](http://www.libpng.org/pub/png/libpng.html) | 1.6.55 | PNG Reference Library License v2 |
| [libtiff](https://gitlab.com/libtiff/libtiff) | 4.7.1 | libtiff (BSD-style) |
| [libwebp](https://chromium.googlesource.com/webm/libwebp) incl. libsharpyuv | 1.6.0 | BSD-3-Clause |
| [OpenJPEG](https://github.com/uclouvain/openjpeg) | 2.5.3 | BSD-2-Clause |
| [OpenEXR](https://github.com/AcademySoftwareFoundation/openexr) (IlmImf) | 2.3.0 | BSD-3-Clause |
| [Protocol Buffers](https://github.com/protocolbuffers/protobuf) | 3.19.1 | BSD-3-Clause |
| [Intel ITT API](https://github.com/intel/ittapi) | 3.25.4 | BSD-3-Clause (dual-licensed; used under BSD-3-Clause) |
| Intel IPP ICV (OpenCV's `3rdparty/ippicv`) | 2022.2.0 | Intel Simplified Software License |

The full licence texts ship with the respective projects; OpenCV collects them in its
source tree under `3rdparty/` and in `LICENSE`. The Intel Simplified Software License
permits redistribution of the IPP ICV binaries in this form.

---

## Tesseract 5.2.0 (tesseract-ocr / Tesseract .NET) — Apache-2.0

<https://github.com/tesseract-ocr/tesseract> ·
<https://github.com/charlesw/tesseract>

Optional engine for the **chat reader**. Native libraries (`tesseract50.dll`,
`leptonica-1.82.0.dll`) are embedded and extracted on first use; `eng.traineddata`
downloads on demand. Leptonica is BSD-2-Clause.

No longer used by the hover reader — see `RAPIDOCR-EVALUATION.md`.

---

## Piper — MIT

<https://github.com/rhasspy/piper>

Local neural text-to-speech. Voices are downloaded on first use and carry their own
licences, most commonly CC-BY-4.0 or MIT; each voice's licence travels with it.

## Whisper.net 1.9.1 / whisper.cpp — MIT

<https://github.com/sandrohanea/whisper.net> · <https://github.com/ggerganov/whisper.cpp>

Local speech recognition for **Voice to Chat** and the spoken wiki search. It is the
optional engine: the default is the speech recognition built into Windows, and Whisper is
used only when it is chosen in Settings → Voice. Its model (tiny, base or small) downloads
once on first use and then runs entirely on this machine.

### Microsoft.Extensions.AI.Abstractions 10.2.0 — MIT

<https://github.com/dotnet/extensions>

Interface definitions that arrive as a dependency of Whisper.net. No model and no service:
nothing in Echoes Unseen sends anything to an AI provider through it.

## NVDA Controller Client — LGPL-2.1

<https://github.com/nvaccess/nvda>

`nvdaControllerClient64.dll` is redistributed unmodified so speech can be routed
through NVDA when the player prefers one voice for everything. Used as a dynamically
loaded library; the LGPL's relinking requirement is satisfied by it remaining a
separate, replaceable DLL.

## AutoHotkey — GPL-2.0

<https://www.autohotkey.com/>

`AutoHotkey64.exe` is redistributed unmodified and launched as a separate process for
the music auto-play feature. It is not linked into Echoes Unseen; running an unmodified
GPL program as a child process does not extend the GPL to this application.

## NAudio 2.2.1 — MIT · CommunityToolkit.Mvvm 8.3.2 — MIT · System.Speech 8.0.0 — MIT · System.Drawing.Common 8.0.0 — MIT

---

## Guild Wars 2

Guild Wars 2 and its assets are © ArenaNet, LLC. Echoes Unseen is an unofficial,
unaffiliated accessibility tool. It reads the screen and the public Guild Wars 2 API;
it does not modify, inject into, or automate the game.

## Marker packs

The guide feature reads the open TacO / BlishHUD marker-pack format. No marker pack is
bundled or redistributed — players supply their own. Enormous thanks to GW2TacO,
BlishHUD, and every author who has drawn and freely shared a trail.

GW2TacO itself is CC BY-NC 4.0; no GW2TacO source is used in Echoes Unseen.

---

## ElevenLabs — optional, cloud, off by default

Used only if the player enters their own API key. Nothing is sent to ElevenLabs
otherwise. This is the only component on this page that can leave the machine, and it
never does so unasked.
