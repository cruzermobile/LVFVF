# LVFVF

LVFVF is an experiment in vector-ish video. The current encoder writes `LVFVF2`, which traces filled image regions instead of Canny edge lines.

It is not a finished codec, but the structure is closer to a practical vector-video direction:

- frames are split into a limited color palette
- connected color regions are traced as polygons
- small regions are dropped according to a quality target
- region boundaries are simplified according to that same quality target
- preprocessing keeps more real detail at normal/high quality instead of blurring everything into soft blobs
- the encoder renders its own first pass, finds visible leftover error, groups that error by source color, and stores targeted correction regions
- regions are matched against the previous frame to keep stable shape IDs when possible

## Commands

```powershell
dotnet run -- convert input.mp4 output.lvfb
dotnet run -- convert input.mp4 output.lvfb --quality 88 --palette 42
dotnet run -- convert input.mp4 output.lvfb --quality 82 --dark-filter 65 --patch-detail 35 --object-focus 45 --corrections 100
dotnet run -- convert input.mp4 output.lvfb --quality 88 --accel auto --pipeline 4 --profile
dotnet run -- convert input.mp4 output.lvfb --tracer merged-fast --corrections 0 --profile
dotnet run -- convert input.mp4 output.lvfb --tracer merged --profile
dotnet run -- convert input.mp4 output.lvfb --tracer custom --profile
dotnet run -- play output.lvfb
dotnet run -- play output.lvfb --renderer gpu
dotnet run -- play-gpu output.lvfb
dotnet run -- info output.lvfb
dotnet run -- gpu-info
```

## GitHub Sync

This folder includes small PowerShell helpers for source-only GitHub syncing. Generated videos and `.lvfb` outputs are ignored by `.gitignore` because they get large quickly.

This local folder is set up to use:

```text
https://github.com/cruzermobile/LVFVF.git
```

To connect or reconnect the folder to that repository:

```powershell
.\scripts\connect-github.ps1 -RepoUrl https://github.com/cruzermobile/LVFVF.git
```

To download your dad's changes without uploading anything:

```powershell
.\scripts\pull-from-github.ps1
```

To download first, then upload a source snapshot manually:

```powershell
.\scripts\sync-to-github.ps1
```

To keep watching for source changes, auto-push after the files are quiet for 20 seconds, and auto-download GitHub changes while the folder is clean:

```powershell
.\scripts\watch-and-sync.ps1
```

Options:

- `--quality`: 1-100. Higher values keep smaller regions and simplify boundaries less.
- `--palette`: number of colors/region groups used during tracing. If omitted, it is chosen from quality.
- `--dark-filter`: 0-100. Default is `65`. Suppresses small, thin, or scattered near-black regions that usually show up as old-film speckles after vectorization. Use `0` to disable it, lower values to keep more dark detail, or higher values to clean harder.
- `--patch-detail`: 0-100. Default is `35`. Adds shape-aware interior correction patches inside large flat regions when the original frame visibly disagrees with the one-color vector fill. The patches are clipped to the region currently visible in the rendered base frame so they do not repaint neighboring shapes. Use `0` for the older flatter look, or higher values when faces, clothing, water, or shadows look too posterized.
- `--object-focus`: 0-100. Default is `45`. Builds a lightweight motion foreground mask from the previous frame and biases patch detail toward moving foreground objects instead of spending the same budget on busy background texture. Try `70`-`85` when people or objects need more attention; use `0` for camera pans, cuts, or footage where motion masking hurts more than it helps.
- `--corrections`: 0-100. Default is `100`. Controls residual correction overlays drawn after the base vector frame. Use `0` to inspect the raw base segmentation without correction patches, or lower values like `25`-`60` if corrections are painting over too much.
- `--tracer`: `opencv`, `merged-fast`, `merged`, or `custom`. `opencv` is still the default. `merged-fast` uses a cheap blur/color-snap prepass before the normal OpenCV contour path and is the practical test path for neighbor-style grouping. `merged` is the slower diagnostic union/merge tracer. `custom` uses the older experimental LVFVF label-boundary tracer.
- `--pipeline`: number of frames to process at once. `1` is the default/stable path. `3` or `4` can smooth GPU/CPU utilization and improve wall-clock time, but uses more RAM and disables frame-to-frame palette reuse.
- `--accel`: `auto`, `cpu`, `cuda`, `opencl`, `hybrid`, or `ffmpeg`.
- `--compression`: `optimal`, `fast`, or `smallest`. `optimal` is the default and is usually the best balance for `.lvfb`.
- `--profile`: prints per-stage encode timing so bottlenecks are visible.

Playback:

- normal `play` uses the older OpenCV renderer.
- `play --renderer gpu` and `play-gpu` use the new OpenGL renderer for `.lvfb` files.

Use `.lvfb` for compressed binary output. `.lvfz` still writes Brotli-compressed text for debugging, and plain `.lvf` still works as raw text, but both text forms are bulkier than the binary format.

FFmpeg and ffprobe need to be available on `PATH` for conversion.

## Acceleration

`--accel auto` is the default. It uses CUDA palette-label assignment when ILGPU can see an NVIDIA CUDA device. If CUDA is not available, it falls back to OpenCL preprocessing when OpenCV can see a compatible GPU. It also asks FFmpeg to try hardware decode and parallelizes the remaining CPU work.

Modes:

- `auto`: CUDA label assignment when available; otherwise OpenCL preprocessing if available; FFmpeg hardware decode attempt; CPU parallel tracing.
- `cuda`: CUDA label assignment through ILGPU and FFmpeg hardware decode attempt.
- `opencl`: OpenCL preprocessing only.
- `hybrid`: CUDA label assignment plus OpenCL preprocessing, useful to test but not always faster because it can add GPU transfer overhead.
- `ffmpeg`: FFmpeg hardware decode attempt only.
- `cpu`: software decode and CPU preprocessing, while still using CPU worker threads for region tracing.

This is not a full GPU codec yet. The default contour extraction and residual correction stages still run through OpenCV's CPU path, but palette labeling now has a real CUDA path, the mask-building stage avoids repeated full-frame scans, and neighboring frames reuse/adapt the palette to avoid reclustering from scratch every frame. Playback has a separate GPU path that tessellates LVFVF2 polygons and draws them through OpenGL instead of OpenCV.

The experimental `--tracer merged-fast` path applies a tiny blur and snaps very close colors together before the usual palette/contour pass. It is meant for quick testing of neighbor-style grouping without the very slow union/merge tracer.

The experimental `--tracer merged` path follows a fuller region-merge pipeline instead of a palette-first pipeline. It starts from adjacent same/similar pixels in the original frame, uses a tiny blur only to decide whether neighboring groups should merge, then builds a group-derived merge palette so large background areas do not dominate the merge decision as strongly. Region colors are averaged from their source pixels rather than copied directly from the palette. This is a diagnostic quality experiment and can still look worse than the default path because it does not yet understand semantic object boundaries.

The experimental `--tracer custom` path flood-fills connected palette-label regions, traces their pixel-boundary edges directly, and simplifies the resulting outline without OpenCV `FindContours`. It is currently useful for development, but OpenCV remains faster on the tested 1080p sample.

`--pipeline` overlaps frames so the GPU-facing stages and CPU-heavy stages are less likely to take turns idling. This is why GPU usage may look spiky with the normal single-frame path: the encoder goes GPU burst, CPU tracing, GPU burst, CPU tracing. Pipelining keeps several frames in flight so those stages can overlap.

## GPU Playback

The GPU player currently supports `.lvfb` region files. It reads compressed binary LVFVF2 frames, converts polygons to triangles, uploads them to a vertex buffer, and lets the graphics card draw the frame. This is the first replacement piece for a future GPU-first engine.

## LVFVF2 Format

The default region format is `.lvfb`: a Brotli-compressed binary stream with a compact LVFVF2 header, frame markers, raw RGB color bytes, variable-length integers for IDs/counts/coordinates, a fallback background color, traced region polygons, and residual correction polygons.

For debugging, `.lvfz` and `.lvf` use a readable text form that starts with:

```text
LVFVF2
FPS 30
SIZE 1920 1080
QUALITY 82
PALETTE 36
```

Every frame stores filled traced regions:

```text
FRAME 0
BACKGROUND 82 118 201
REGION 17 84 120 210 4 10,10 40,11 39,50 12,48
REGION 0 92 106 188 4 45,20 54,21 53,30 44,29
END
```

`BACKGROUND` is the dominant frame color. Normal regions use stable positive IDs. `REGION 0` is an untracked residual correction overlay produced by comparing the encoded pass against the source frame. The correction pass groups residuals by color first so it does not turn mixed-color leftover detail into one muddy blob.

`REGION` fields are:

```text
REGION <stable-id> <r> <g> <b> <point-count> <x,y>...
```

The player also still reads:

- `.lvfb` compressed binary region files
- `.lvfz` compressed text region files
- `LVFVF1` edge-path files
- older `fps,width,height` point-stream files

The old edge encoder is still available for comparison:

```powershell
dotnet run -- convert-edges input.mp4 output.lvfz --sensitivity 80 --simplify 1.75 --accel auto
```
