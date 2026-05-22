using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

partial class Program
{
    private const byte StrokeFileVersion = 1;
    private const byte StrokeFrameMarker = 0xF5;
    private const int StrokePlaybackBufferSize = 72;
    private static readonly byte[] StrokeMagic = Encoding.ASCII.GetBytes("LVFS1");

    private static int ConvertStrokesCommand(List<string> args)
    {
        bool profile = TakeFlag(args, "--profile");
        CompressionLevel compressionLevel = TakeCompressionOption(args);
        int quality = TakeIntOption(args, DefaultQuality, 1, 100, "--quality", "-q");
        int strokeDensity = TakeIntOption(args, 60, 0, 100, "--stroke-density", "--strokes");
        int surfaceDetail = TakeIntOption(args, 35, 0, 100, "--surface-detail", "--surfaces");
        int residual = TakeIntOption(args, 20, 0, 100, "--residual", "--residuals");
        int glow = TakeIntOption(args, 55, 0, 100, "--glow");
        int keyframeInterval = TakeIntOption(args, 30, 1, 600, "--keyframe", "--keyframes", "--keyframe-interval");
        int pipelineDepth = TakeIntOption(args, 4, 1, Math.Max(1, Environment.ProcessorCount / 2), "--pipeline", "--parallel-frames");
        int maxFrames = TakeIntOption(args, 0, 0, int.MaxValue, "--max-frames", "--frames");
        using AccelerationOptions acceleration = CreateAccelerationOptions(TakeAccelerationOption(args));

        if (args.Count is < 1 or > 2)
        {
            PrintUsage();
            return 1;
        }

        string input = args[0];
        string output = args.Count == 2 ? args[1] : Path.ChangeExtension(input, ".lvfs") ?? $"{input}.lvfs";
        StrokeEncodeOptions options = new(
            quality,
            strokeDensity,
            surfaceDetail,
            residual,
            glow,
            keyframeInterval,
            pipelineDepth,
            compressionLevel,
            profile,
            maxFrames);
        ProcessVideoStrokes(input, output, options, acceleration);
        return 0;
    }

    private static bool IsStrokeLvfPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".lvfs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lvfsz", StringComparison.OrdinalIgnoreCase);
    }

    private static void ProcessVideoStrokes(string videoPath, string outputPath, StrokeEncodeOptions options, AccelerationOptions acceleration)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file was not found.", videoPath);
        }

        VideoInfo info = ProbeVideo(videoPath);
        int channels = 3;
        int frameSize = checked(info.Width * info.Height * channels);
        int surfaceCellSize = StrokeSurfaceCellSize(info.Width, info.Height, options.Quality, options.SurfaceDetail);
        int surfaceColumns = DivRoundUp(info.Width, surfaceCellSize);
        int surfaceRows = DivRoundUp(info.Height, surfaceCellSize);

        Console.WriteLine($"Encoding {videoPath}");
        Console.WriteLine($"Input: {info.Width}x{info.Height} @ {FormatDouble(info.Fps)} fps");
        Console.WriteLine($"Stroke hybrid: quality {options.Quality}, stroke density {options.StrokeDensity}, surface detail {options.SurfaceDetail}, residual {options.ResidualStrength}, glow {options.Glow}");
        Console.WriteLine($"Surface grid: {surfaceColumns}x{surfaceRows} cells ({surfaceCellSize}px target)");
        Console.WriteLine($"Keyframes: every {options.KeyframeInterval} frame(s)");
        Console.WriteLine($"Pipeline: temporal tracking path, {options.PipelineDepth} requested (decode/analysis kept ordered for stable IDs)");
        if (options.MaxFrames > 0)
        {
            Console.WriteLine($"Max frames: {options.MaxFrames}");
        }

        Console.WriteLine($"Acceleration: {DescribeAcceleration(acceleration)}");
        Console.WriteLine($"Compression: {DescribeCompression(options.CompressionLevel)}");
        Console.WriteLine("OpenCV: not used by convert-strokes hot path");

        ConfigureAcceleration(acceleration);

        StrokeHeader header = new(
            info.Width,
            info.Height,
            info.Fps,
            options.Quality,
            options.StrokeDensity,
            options.SurfaceDetail,
            options.ResidualStrength,
            options.Glow,
            options.KeyframeInterval,
            surfaceCellSize,
            surfaceColumns,
            surfaceRows);

        using Process ffmpeg = StartFfmpegRawVideo(videoPath, "bgr24", acceleration);
        byte[] buffer = new byte[frameSize];
        using StrokeFrameWriter writer = new(outputPath, options.CompressionLevel);
        writer.WriteHeader(header);

        StrokeEncodeState state = new(header);
        StrokeEncodeProfiler? profiler = options.Profile ? new StrokeEncodeProfiler() : null;
        int frameCount = 0;
        long totalSurfaceChanges = 0;
        long totalStrokes = 0;
        long totalStrokePoints = 0;
        long totalResiduals = 0;
        bool stoppedEarly = false;

        while (ReadFullFrame(ffmpeg.StandardOutput.BaseStream, buffer))
        {
            byte[] frameBytes = buffer.ToArray();
            long frameStart = Stopwatch.GetTimestamp();
            StrokeFrame frame = TraceStrokeFrame(frameCount, frameBytes, header, options, state, profiler);
            profiler?.Add(StrokeEncodeStage.FrameTotal, frameStart);

            long writeStart = Stopwatch.GetTimestamp();
            writer.WriteFrame(frame);
            profiler?.Add(StrokeEncodeStage.Write, writeStart);

            totalSurfaceChanges += frame.SurfaceChanges.Count;
            totalStrokes += frame.Strokes.Count;
            totalStrokePoints += frame.Strokes.Sum(stroke => stroke.Points.Count);
            totalResiduals += frame.Residuals.Count;
            frameCount++;

            if (frameCount % 10 == 0)
            {
                Console.WriteLine($"Encoded {frameCount} frames | {totalStrokes} strokes | {totalStrokePoints} stroke points | {totalResiduals} residual patches");
            }

            if (options.MaxFrames > 0 && frameCount >= options.MaxFrames)
            {
                stoppedEarly = true;
                break;
            }
        }

        if (stoppedEarly && !ffmpeg.HasExited)
        {
            ffmpeg.Kill(entireProcessTree: true);
        }
        else
        {
            ffmpeg.WaitForExit();
        }

        if (!stoppedEarly && ffmpeg.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}.");
        }

        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine($"Frames: {frameCount}, surface changes: {totalSurfaceChanges}, strokes: {totalStrokes}, stroke points: {totalStrokePoints}, residual patches: {totalResiduals}");
        profiler?.Print(frameCount);
    }

    private static StrokeFrame TraceStrokeFrame(int frameNumber, byte[] sourceBgr, StrokeHeader header, StrokeEncodeOptions options, StrokeEncodeState state, StrokeEncodeProfiler? profiler)
    {
        bool isKeyframe = frameNumber == 0 || frameNumber % options.KeyframeInterval == 0 || state.SurfaceColors is null;

        long surfaceStart = Stopwatch.GetTimestamp();
        Color[] currentSurface = BuildSurfaceCells(sourceBgr, header);
        List<StrokeSurfaceChange> surfaceChanges = BuildSurfaceChanges(currentSurface, state.SurfaceColors, isKeyframe, options.Quality, options.SurfaceDetail);
        state.SurfaceColors = currentSurface;
        profiler?.Add(StrokeEncodeStage.Surfaces, surfaceStart);

        long analysisStart = Stopwatch.GetTimestamp();
        byte[] luminance = BuildStrokeLuminance(sourceBgr, header.Width, header.Height);
        byte[] blurred = BlurLuminance3x3(luminance, header.Width, header.Height);
        GradientField gradient = BuildGradientField(blurred, header.Width, header.Height);
        profiler?.Add(StrokeEncodeStage.Analysis, analysisStart);

        long edgeStart = Stopwatch.GetTimestamp();
        byte[] edgeMask = BuildAdaptiveEdgeMask(gradient, header.Width, header.Height, options.Quality, options.StrokeDensity);
        BridgeSmallEdgeGaps(edgeMask, header.Width, header.Height);
        profiler?.Add(StrokeEncodeStage.Edges, edgeStart);

        long linkStart = Stopwatch.GetTimestamp();
        List<StrokePrimitive> strokes = BuildStrokePrimitives(edgeMask, gradient, sourceBgr, header.Width, header.Height, options);
        profiler?.Add(StrokeEncodeStage.LinkStrokes, linkStart);

        long trackStart = Stopwatch.GetTimestamp();
        AssignStrokeIdsAndSmooth(strokes, state, header.Width, header.Height, options);
        profiler?.Add(StrokeEncodeStage.TrackStrokes, trackStart);

        long residualStart = Stopwatch.GetTimestamp();
        List<StrokeResidualPatch> residuals = BuildStrokeResiduals(sourceBgr, currentSurface, strokes, header, options);
        profiler?.Add(StrokeEncodeStage.Residuals, residualStart);

        return new StrokeFrame(frameNumber, isKeyframe, surfaceChanges, strokes, residuals);
    }

    private static Color[] BuildSurfaceCells(byte[] sourceBgr, StrokeHeader header)
    {
        Color[] cells = new Color[checked(header.SurfaceColumns * header.SurfaceRows)];
        Parallel.For(0, header.SurfaceRows, yCell =>
        {
            int y0 = yCell * header.SurfaceCellSize;
            int y1 = Math.Min(header.Height, y0 + header.SurfaceCellSize);
            for (int xCell = 0; xCell < header.SurfaceColumns; xCell++)
            {
                int x0 = xCell * header.SurfaceCellSize;
                int x1 = Math.Min(header.Width, x0 + header.SurfaceCellSize);
                long sumB = 0;
                long sumG = 0;
                long sumR = 0;
                int count = 0;

                for (int y = y0; y < y1; y++)
                {
                    int offset = (y * header.Width + x0) * 3;
                    for (int x = x0; x < x1; x++, offset += 3)
                    {
                        sumB += sourceBgr[offset];
                        sumG += sourceBgr[offset + 1];
                        sumR += sourceBgr[offset + 2];
                        count++;
                    }
                }

                cells[yCell * header.SurfaceColumns + xCell] = count == 0
                    ? Color.Black
                    : Color.FromArgb((int)(sumR / count), (int)(sumG / count), (int)(sumB / count));
            }
        });

        return cells;
    }

    private static List<StrokeSurfaceChange> BuildSurfaceChanges(Color[] current, Color[]? previous, bool isKeyframe, int quality, int surfaceDetail)
    {
        int threshold = SurfaceDeltaThreshold(quality, surfaceDetail);
        List<StrokeSurfaceChange> changes = new(current.Length);
        for (int i = 0; i < current.Length; i++)
        {
            if (isKeyframe || previous is null || ColorDistance(current[i], previous[i]) >= threshold)
            {
                changes.Add(new StrokeSurfaceChange(i, current[i]));
            }
        }

        return changes;
    }

    private static byte[] BuildStrokeLuminance(byte[] sourceBgr, int width, int height)
    {
        int pixelCount = checked(width * height);
        byte[] luminance = new byte[pixelCount];
        Parallel.For(0, pixelCount, i =>
        {
            int offset = i * 3;
            int b = sourceBgr[offset];
            int g = sourceBgr[offset + 1];
            int r = sourceBgr[offset + 2];
            luminance[i] = (byte)((r * 54 + g * 183 + b * 19) >> 8);
        });

        return luminance;
    }

    private static byte[] BlurLuminance3x3(byte[] source, int width, int height)
    {
        byte[] blurred = new byte[source.Length];
        if (width < 3 || height < 3)
        {
            Array.Copy(source, blurred, source.Length);
            return blurred;
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    blurred[y * width + x] = source[y * width + x];
                    continue;
                }

                int row = y * width;
                int value =
                    source[row - width + x - 1] + source[row - width + x + 1] +
                    source[row + width + x - 1] + source[row + width + x + 1] +
                    ((source[row - width + x] + source[row + width + x] + source[row + x - 1] + source[row + x + 1]) << 1) +
                    (source[row + x] << 2);
                blurred[row + x] = (byte)((value + 8) >> 4);
            }
        });

        return blurred;
    }

    private static GradientField BuildGradientField(byte[] luminance, int width, int height)
    {
        byte[] magnitude = new byte[luminance.Length];
        short[] gxValues = new short[luminance.Length];
        short[] gyValues = new short[luminance.Length];

        if (width < 3 || height < 3)
        {
            return new GradientField(magnitude, gxValues, gyValues);
        }

        Parallel.For(1, height - 1, y =>
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                int gx =
                    -luminance[i - width - 1] + luminance[i - width + 1] +
                    ((-luminance[i - 1] + luminance[i + 1]) << 1) +
                    -luminance[i + width - 1] + luminance[i + width + 1];
                int gy =
                    -luminance[i - width - 1] - (luminance[i - width] << 1) - luminance[i - width + 1] +
                    luminance[i + width - 1] + (luminance[i + width] << 1) + luminance[i + width + 1];
                int mag = Math.Min(255, (Math.Abs(gx) + Math.Abs(gy)) >> 2);
                gxValues[i] = (short)Math.Clamp(gx, short.MinValue, short.MaxValue);
                gyValues[i] = (short)Math.Clamp(gy, short.MinValue, short.MaxValue);
                magnitude[i] = (byte)mag;
            }
        });

        return new GradientField(magnitude, gxValues, gyValues);
    }

    private static byte[] BuildAdaptiveEdgeMask(GradientField gradient, int width, int height, int quality, int strokeDensity)
    {
        byte[] thinned = NonMaximumSuppress(gradient, width, height);
        int high = AdaptiveHighThreshold(thinned, quality, strokeDensity);
        int low = Math.Max(8, (int)Math.Round(high * 0.42));
        byte[] mask = new byte[thinned.Length];
        Queue<int> queue = new();

        for (int i = 0; i < thinned.Length; i++)
        {
            if (thinned[i] >= high)
            {
                mask[i] = 2;
                queue.Enqueue(i);
            }
        }

        int[] neighbors = { -width - 1, -width, -width + 1, -1, 1, width - 1, width, width + 1 };
        while (queue.Count > 0)
        {
            int pixel = queue.Dequeue();
            int x = pixel % width;
            int y = pixel / width;
            for (int n = 0; n < neighbors.Length; n++)
            {
                int nx = x + NeighborDx(n);
                int ny = y + NeighborDy(n);
                if (nx <= 0 || nx >= width - 1 || ny <= 0 || ny >= height - 1)
                {
                    continue;
                }

                int next = pixel + neighbors[n];
                if (mask[next] == 0 && thinned[next] >= low)
                {
                    mask[next] = 2;
                    queue.Enqueue(next);
                }
            }
        }

        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = mask[i] == 2 ? (byte)1 : (byte)0;
        }

        return mask;
    }

    private static byte[] NonMaximumSuppress(GradientField gradient, int width, int height)
    {
        byte[] output = new byte[gradient.Magnitude.Length];
        if (width < 3 || height < 3)
        {
            return output;
        }

        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int mag = gradient.Magnitude[i];
                if (mag == 0)
                {
                    continue;
                }

                int gx = gradient.Gx[i];
                int gy = gradient.Gy[i];
                int agx = Math.Abs(gx);
                int agy = Math.Abs(gy);
                int before;
                int after;

                if (agx > agy * 2)
                {
                    before = gradient.Magnitude[i - 1];
                    after = gradient.Magnitude[i + 1];
                }
                else if (agy > agx * 2)
                {
                    before = gradient.Magnitude[i - width];
                    after = gradient.Magnitude[i + width];
                }
                else if ((gx ^ gy) >= 0)
                {
                    before = gradient.Magnitude[i - width - 1];
                    after = gradient.Magnitude[i + width + 1];
                }
                else
                {
                    before = gradient.Magnitude[i - width + 1];
                    after = gradient.Magnitude[i + width - 1];
                }

                if (mag >= before && mag >= after)
                {
                    output[i] = (byte)mag;
                }
            }
        });

        return output;
    }

    private static int AdaptiveHighThreshold(byte[] magnitude, int quality, int strokeDensity)
    {
        Span<int> histogram = stackalloc int[256];
        int nonZero = 0;
        foreach (byte value in magnitude)
        {
            if (value == 0)
            {
                continue;
            }

            histogram[value]++;
            nonZero++;
        }

        if (nonZero == 0)
        {
            return 255;
        }

        double keepPercent = 0.012 + strokeDensity * 0.00075 + quality * 0.00022;
        int keep = Math.Clamp((int)Math.Round(nonZero * keepPercent), Math.Max(80, nonZero / 300), Math.Max(1, nonZero / 6));
        int accumulated = 0;
        for (int value = 255; value >= 1; value--)
        {
            accumulated += histogram[value];
            if (accumulated >= keep)
            {
                return Math.Clamp(value, 18, 210);
            }
        }

        return 32;
    }

    private static void BridgeSmallEdgeGaps(byte[] edgeMask, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return;
        }

        byte[] bridge = new byte[edgeMask.Length];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (edgeMask[i] != 0)
                {
                    continue;
                }

                bool shouldBridge =
                    (edgeMask[i - 1] != 0 && edgeMask[i + 1] != 0) ||
                    (edgeMask[i - width] != 0 && edgeMask[i + width] != 0) ||
                    (edgeMask[i - width - 1] != 0 && edgeMask[i + width + 1] != 0) ||
                    (edgeMask[i - width + 1] != 0 && edgeMask[i + width - 1] != 0);
                if (shouldBridge)
                {
                    bridge[i] = 1;
                }
            }
        }

        for (int i = 0; i < edgeMask.Length; i++)
        {
            edgeMask[i] = (byte)Math.Min(1, edgeMask[i] + bridge[i]);
        }
    }

    private static List<StrokePrimitive> BuildStrokePrimitives(byte[] edgeMask, GradientField gradient, byte[] sourceBgr, int width, int height, StrokeEncodeOptions options)
    {
        bool[] visited = new bool[edgeMask.Length];
        int[] componentMark = new int[edgeMask.Length];
        int markToken = 1;
        List<StrokePrimitive> strokes = new();
        Queue<int> queue = new();
        List<int> component = new();
        int minPixels = StrokeMinComponentPixels(width, height, options.Quality, options.StrokeDensity);
        double simplify = StrokeSimplifyForQuality(options.Quality, options.StrokeDensity);
        double minLength = StrokeMinLength(width, height, options.Quality, options.StrokeDensity);

        for (int pixel = 0; pixel < edgeMask.Length; pixel++)
        {
            if (edgeMask[pixel] == 0 || visited[pixel])
            {
                continue;
            }

            component.Clear();
            queue.Clear();
            visited[pixel] = true;
            queue.Enqueue(pixel);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                component.Add(current);
                int x = current % width;
                int y = current / width;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        int next = ny * width + nx;
                        if (edgeMask[next] != 0 && !visited[next])
                        {
                            visited[next] = true;
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            if (component.Count < minPixels)
            {
                continue;
            }

            int token = markToken++;
            foreach (int index in component)
            {
                componentMark[index] = token;
            }

            List<Point> ordered = OrderStrokeComponent(component, componentMark, token, width, height);
            if (ordered.Count < 2)
            {
                continue;
            }

            List<Point> simplified = SimplifyStrokePath(ordered, simplify);
            double length = StrokePathLength(simplified);
            if (length < minLength)
            {
                continue;
            }

            StrokePrimitive stroke = BuildStrokePrimitive(simplified, gradient, sourceBgr, width, height, options);
            strokes.Add(stroke);
        }

        int maxStrokes = MaxStrokesForFrame(width, height, options.StrokeDensity);
        return strokes
            .OrderByDescending(StrokePriority)
            .Take(maxStrokes)
            .OrderBy(stroke => stroke.Bounds.Top)
            .ThenBy(stroke => stroke.Bounds.Left)
            .ToList();
    }

    private static List<Point> OrderStrokeComponent(List<int> component, int[] componentMark, int token, int width, int height)
    {
        int start = component[0];
        int bestNeighborCount = 9;
        foreach (int pixel in component)
        {
            int count = CountMarkedNeighbors(pixel, componentMark, token, width, height);
            if (count < bestNeighborCount)
            {
                bestNeighborCount = count;
                start = pixel;
                if (count <= 1)
                {
                    break;
                }
            }
        }

        HashSet<int> remaining = component.ToHashSet();
        List<Point> points = new(component.Count);
        int current = start;
        int previous = -1;

        while (remaining.Remove(current))
        {
            points.Add(new Point(current % width, current / width));
            int next = FindNextStrokePixel(current, previous, remaining, width, height);
            if (next < 0)
            {
                break;
            }

            previous = current;
            current = next;
        }

        if (remaining.Count > 0 && points.Count < component.Count / 2)
        {
            foreach (int pixel in remaining)
            {
                points.Add(new Point(pixel % width, pixel / width));
            }
        }

        return points;
    }

    private static int FindNextStrokePixel(int current, int previous, HashSet<int> remaining, int width, int height)
    {
        int currentX = current % width;
        int currentY = current / width;
        int previousDx = previous < 0 ? 0 : currentX - previous % width;
        int previousDy = previous < 0 ? 0 : currentY - previous / width;
        int best = -1;
        int bestScore = int.MaxValue;

        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = currentY + dy;
            if (ny < 0 || ny >= height)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = currentX + dx;
                if (nx < 0 || nx >= width)
                {
                    continue;
                }

                int candidate = ny * width + nx;
                if (!remaining.Contains(candidate))
                {
                    continue;
                }

                int turnPenalty = previous < 0 ? 0 : Math.Abs(dx - previousDx) + Math.Abs(dy - previousDy);
                int diagonalPenalty = dx != 0 && dy != 0 ? 1 : 0;
                int score = turnPenalty * 3 + diagonalPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static int CountMarkedNeighbors(int pixel, int[] marks, int token, int width, int height)
    {
        int x = pixel % width;
        int y = pixel / width;
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= height)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = x + dx;
                if (nx < 0 || nx >= width)
                {
                    continue;
                }

                if (marks[ny * width + nx] == token)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static StrokePrimitive BuildStrokePrimitive(List<Point> points, GradientField gradient, byte[] sourceBgr, int width, int height, StrokeEncodeOptions options)
    {
        long sumB = 0;
        long sumG = 0;
        long sumR = 0;
        long sumMag = 0;
        int samples = 0;
        int sampleStep = Math.Max(1, points.Count / 96);

        for (int i = 0; i < points.Count; i += sampleStep)
        {
            Point point = points[i];
            int offset = (point.Y * width + point.X) * 3;
            sumB += sourceBgr[offset];
            sumG += sourceBgr[offset + 1];
            sumR += sourceBgr[offset + 2];
            sumMag += gradient.Magnitude[point.Y * width + point.X];
            samples++;
        }

        if (samples == 0)
        {
            samples = 1;
        }

        Color color = Color.FromArgb((int)(sumR / samples), (int)(sumG / samples), (int)(sumB / samples));
        byte intensity = (byte)Math.Clamp((int)Math.Round(sumMag / (double)samples * 1.2 + options.Quality * 0.35), 32, 255);
        byte widthByte = (byte)Math.Clamp((int)Math.Round((1.05 + options.Quality * 0.012 + options.StrokeDensity * 0.004) * 4), 4, 18);
        byte glowByte = (byte)Math.Clamp((int)Math.Round(1 + options.Glow * 0.16 + intensity * 0.025), 0, 32);
        byte opacity = (byte)Math.Clamp(120 + intensity / 2 + options.Glow / 3, 120, 255);
        Rectangle bounds = BoundsFor(points);
        PointF center = AveragePoint(points);
        return new StrokePrimitive(0, points, color, intensity, widthByte, glowByte, opacity, bounds, center, StrokePathLength(points), 0);
    }

    private static void AssignStrokeIdsAndSmooth(List<StrokePrimitive> strokes, StrokeEncodeState state, int width, int height, StrokeEncodeOptions options)
    {
        double diagonal = Math.Sqrt(width * width + height * height);
        HashSet<int> usedPrevious = new();
        foreach (StrokePrimitive stroke in strokes.OrderByDescending(StrokePriority))
        {
            TrackedStroke? best = null;
            double bestScore = double.MaxValue;
            foreach (TrackedStroke previous in state.PreviousStrokes)
            {
                if (usedPrevious.Contains(previous.Id))
                {
                    continue;
                }

                double score = StrokeMatchScore(stroke, previous, diagonal);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = previous;
                }
            }

            if (best is not null && bestScore <= 0.22)
            {
                stroke.Id = best.Id;
                SmoothStroke(stroke, best);
                usedPrevious.Add(best.Id);
            }
            else
            {
                stroke.Id = state.NextStrokeId++;
            }
        }

        int ghostLimit = Math.Clamp(options.StrokeDensity, 12, 80);
        foreach (TrackedStroke previous in state.PreviousStrokes
            .Where(previous => !usedPrevious.Contains(previous.Id) && previous.Misses < 2)
            .OrderByDescending(previous => previous.Length)
            .Take(ghostLimit))
        {
            byte opacity = (byte)Math.Max(24, previous.Opacity * (previous.Misses == 0 ? 55 : 35) / 100);
            strokes.Add(new StrokePrimitive(
                previous.Id,
                previous.Points.Select(point => point).ToList(),
                previous.Color,
                previous.Intensity,
                previous.Width,
                previous.Glow,
                opacity,
                previous.Bounds,
                previous.Center,
                previous.Length,
                previous.Misses + 1));
        }

        state.PreviousStrokes = strokes
            .Select(stroke => new TrackedStroke(stroke.Id, stroke.Points.Select(point => point).ToList(), stroke.Color, stroke.Intensity, stroke.Width, stroke.Glow, stroke.Opacity, stroke.Bounds, stroke.Center, stroke.Length, stroke.Misses))
            .ToList();
    }

    private static double StrokeMatchScore(StrokePrimitive current, TrackedStroke previous, double diagonal)
    {
        double center = Distance(current.Center, previous.Center) / Math.Max(1, diagonal);
        double color = ColorDistance(current.Color, previous.Color) / 441.67295593;
        double lengthRatio = Math.Abs(Math.Log((current.Length + 1) / (previous.Length + 1)));
        double length = Math.Clamp(lengthRatio / 2.2, 0, 1);
        double bounds = 1.0 - IntersectionOverUnion(current.Bounds, previous.Bounds);
        double endpoints = StrokeEndpointDistance(current.Points, previous.Points) / Math.Max(1, diagonal);
        return center * 0.30 + endpoints * 0.25 + color * 0.20 + bounds * 0.15 + length * 0.10;
    }

    private static void SmoothStroke(StrokePrimitive stroke, TrackedStroke previous)
    {
        stroke.Color = BlendColor(previous.Color, stroke.Color, 0.70);
        stroke.Intensity = BlendByte(previous.Intensity, stroke.Intensity, 0.70);
        stroke.Width = BlendByte(previous.Width, stroke.Width, 0.72);
        stroke.Glow = BlendByte(previous.Glow, stroke.Glow, 0.70);
        stroke.Opacity = BlendByte(previous.Opacity, stroke.Opacity, 0.78);

        if (stroke.Points.Count == previous.Points.Count)
        {
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                Point oldPoint = previous.Points[i];
                Point newPoint = stroke.Points[i];
                stroke.Points[i] = new Point(
                    (int)Math.Round(oldPoint.X * 0.28 + newPoint.X * 0.72),
                    (int)Math.Round(oldPoint.Y * 0.28 + newPoint.Y * 0.72));
            }

            stroke.Bounds = BoundsFor(stroke.Points);
            stroke.Center = AveragePoint(stroke.Points);
            stroke.Length = StrokePathLength(stroke.Points);
        }
    }

    private static List<StrokeResidualPatch> BuildStrokeResiduals(byte[] sourceBgr, Color[] surfaceColors, List<StrokePrimitive> strokes, StrokeHeader header, StrokeEncodeOptions options)
    {
        if (options.ResidualStrength <= 0)
        {
            return new List<StrokeResidualPatch>();
        }

        byte[] prediction = RenderStrokePrediction(surfaceColors, strokes, header);
        int tileSize = Math.Clamp(header.SurfaceCellSize / 2, 10, 24);
        int columns = DivRoundUp(header.Width, tileSize);
        int rows = DivRoundUp(header.Height, tileSize);
        int threshold = ResidualErrorThreshold(options.Quality, options.ResidualStrength);
        List<StrokeResidualCandidate> candidates = new();

        for (int yCell = 0; yCell < rows; yCell++)
        {
            int y0 = yCell * tileSize;
            int y1 = Math.Min(header.Height, y0 + tileSize);
            for (int xCell = 0; xCell < columns; xCell++)
            {
                int x0 = xCell * tileSize;
                int x1 = Math.Min(header.Width, x0 + tileSize);
                long sumError = 0;
                long sumB = 0;
                long sumG = 0;
                long sumR = 0;
                int count = 0;

                for (int y = y0; y < y1; y++)
                {
                    int offset = (y * header.Width + x0) * 3;
                    for (int x = x0; x < x1; x++, offset += 3)
                    {
                        int db = Math.Abs(sourceBgr[offset] - prediction[offset]);
                        int dg = Math.Abs(sourceBgr[offset + 1] - prediction[offset + 1]);
                        int dr = Math.Abs(sourceBgr[offset + 2] - prediction[offset + 2]);
                        int error = (db + dg + dr) / 3;
                        sumError += error;
                        sumB += sourceBgr[offset];
                        sumG += sourceBgr[offset + 1];
                        sumR += sourceBgr[offset + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                double meanError = sumError / (double)count;
                if (meanError >= threshold)
                {
                    Color color = Color.FromArgb((int)(sumR / count), (int)(sumG / count), (int)(sumB / count));
                    byte opacity = (byte)Math.Clamp(45 + meanError * 4.5, 70, 230);
                    candidates.Add(new StrokeResidualCandidate(meanError, new StrokeResidualPatch(x0, y0, x1 - x0, y1 - y0, color, opacity)));
                }
            }
        }

        int maxResiduals = MaxResidualPatches(options.ResidualStrength, header.Width, header.Height);
        return candidates
            .OrderByDescending(candidate => candidate.Error)
            .Take(maxResiduals)
            .Select(candidate => candidate.Patch)
            .OrderBy(patch => patch.Y)
            .ThenBy(patch => patch.X)
            .ToList();
    }

    private static byte[] RenderStrokePrediction(Color[] surfaceColors, List<StrokePrimitive> strokes, StrokeHeader header)
    {
        byte[] frame = new byte[checked(header.Width * header.Height * 3)];
        RenderSurfaceCellsToBgr(frame, surfaceColors, header);
        foreach (StrokePrimitive stroke in strokes)
        {
            DrawStrokeToBgr(frame, header.Width, header.Height, stroke, Math.Max(1, stroke.Width / 4), 0.85);
        }

        return frame;
    }

    private static void RenderSurfaceCellsToBgr(byte[] frame, Color[] surfaceColors, StrokeHeader header)
    {
        Parallel.For(0, header.SurfaceRows, yCell =>
        {
            int y0 = yCell * header.SurfaceCellSize;
            int y1 = Math.Min(header.Height, y0 + header.SurfaceCellSize);
            for (int xCell = 0; xCell < header.SurfaceColumns; xCell++)
            {
                int x0 = xCell * header.SurfaceCellSize;
                int x1 = Math.Min(header.Width, x0 + header.SurfaceCellSize);
                Color color = surfaceColors[yCell * header.SurfaceColumns + xCell];
                for (int y = y0; y < y1; y++)
                {
                    int offset = (y * header.Width + x0) * 3;
                    for (int x = x0; x < x1; x++, offset += 3)
                    {
                        frame[offset] = color.B;
                        frame[offset + 1] = color.G;
                        frame[offset + 2] = color.R;
                    }
                }
            }
        });
    }

    private static void DrawStrokeToBgr(byte[] frame, int width, int height, StrokePrimitive stroke, int strokeWidth, double alpha)
    {
        if (stroke.Points.Count == 0)
        {
            return;
        }

        int radius = Math.Max(0, strokeWidth / 2);
        for (int i = 1; i < stroke.Points.Count; i++)
        {
            DrawLineToBgr(frame, width, height, stroke.Points[i - 1], stroke.Points[i], stroke.Color, radius, alpha * stroke.Opacity / 255.0);
        }
    }

    private static void DrawLineToBgr(byte[] frame, int width, int height, Point a, Point b, Color color, int radius, double alpha)
    {
        int dx = Math.Abs(b.X - a.X);
        int dy = Math.Abs(b.Y - a.Y);
        int sx = a.X < b.X ? 1 : -1;
        int sy = a.Y < b.Y ? 1 : -1;
        int err = dx - dy;
        int x = a.X;
        int y = a.Y;

        while (true)
        {
            BlendDisk(frame, width, height, x, y, radius, color, alpha);
            if (x == b.X && y == b.Y)
            {
                break;
            }

            int e2 = err * 2;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static void BlendDisk(byte[] frame, int width, int height, int cx, int cy, int radius, Color color, double alpha)
    {
        int r = Math.Max(0, radius);
        int r2 = r * r;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(height - 1, cy + r); y++)
        {
            for (int x = Math.Max(0, cx - r); x <= Math.Min(width - 1, cx + r); x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (r > 0 && dx * dx + dy * dy > r2)
                {
                    continue;
                }

                int offset = (y * width + x) * 3;
                frame[offset] = BlendByte(frame[offset], color.B, alpha);
                frame[offset + 1] = BlendByte(frame[offset + 1], color.G, alpha);
                frame[offset + 2] = BlendByte(frame[offset + 2], color.R, alpha);
            }
        }
    }

    private static void PlayStrokeLVF(string lvfPath)
    {
        using StrokeFrameReader reader = new(lvfPath);
        StrokeHeader header = reader.Header;
        using BlockingCollection<StrokeGpuFrameData> queue = new(StrokePlaybackBufferSize);
        using CancellationTokenSource cancellation = new();
        Exception? producerError = null;

        Task producer = Task.Run(() =>
        {
            try
            {
                while (reader.ReadNextFrame() is { } frame)
                {
                    queue.Add(BuildStrokeGpuFrame(header, frame), cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                producerError = ex;
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        Console.WriteLine($"GPU playing {Path.GetFileName(lvfPath)} at {FormatDouble(header.Fps)} fps ({header.Width}x{header.Height}). Press Q or Esc to stop.");
        using StrokePlaybackWindow window = new(header, queue, Path.GetFileName(lvfPath));
        window.Run();

        cancellation.Cancel();
        producer.Wait();
        if (producerError is not null)
        {
            throw new InvalidDataException($"Stroke playback stopped while reading LVFS data: {producerError.Message}", producerError);
        }

        Console.WriteLine($"GPU playback finished after {window.DisplayedFrames} frames.");
    }

    private static void InspectStrokeLVF(string lvfPath)
    {
        using StrokeFrameReader reader = new(lvfPath);
        StrokeHeader header = reader.Header;
        int frames = 0;
        int keyframes = 0;
        long surfaceChanges = 0;
        long strokes = 0;
        long strokePoints = 0;
        long residuals = 0;
        HashSet<int> strokeIds = new();

        while (reader.ReadNextFrame() is { } frame)
        {
            frames++;
            if (frame.IsKeyframe)
            {
                keyframes++;
            }

            surfaceChanges += frame.SurfaceColors.Length;
            strokes += frame.Strokes.Count;
            residuals += frame.Residuals.Count;
            foreach (StrokePrimitive stroke in frame.Strokes)
            {
                strokeIds.Add(stroke.Id);
                strokePoints += stroke.Points.Count;
            }
        }

        Console.WriteLine($"File: {lvfPath}");
        Console.WriteLine("Format: LVFS1 stroke-hybrid video");
        Console.WriteLine("Storage: compressed binary");
        Console.WriteLine($"Size: {header.Width}x{header.Height}");
        Console.WriteLine($"FPS: {FormatDouble(header.Fps)}");
        Console.WriteLine($"Quality: {header.Quality}");
        Console.WriteLine($"Stroke density: {header.StrokeDensity}");
        Console.WriteLine($"Surface detail: {header.SurfaceDetail}");
        Console.WriteLine($"Residual: {header.ResidualStrength}");
        Console.WriteLine($"Glow: {header.Glow}");
        Console.WriteLine($"Surface grid: {header.SurfaceColumns}x{header.SurfaceRows}");
        Console.WriteLine($"Frames: {frames}");
        Console.WriteLine($"Keyframes: {keyframes}");
        Console.WriteLine($"Surface cells decoded: {surfaceChanges}");
        Console.WriteLine($"Strokes: {strokes}");
        Console.WriteLine($"Unique stroke IDs: {strokeIds.Count}");
        Console.WriteLine($"Stroke points: {strokePoints}");
        Console.WriteLine($"Residual patches: {residuals}");
    }

    private static StrokeGpuFrameData BuildStrokeGpuFrame(StrokeHeader header, DecodedStrokeFrame frame)
    {
        List<float> vertices = new(header.SurfaceColumns * header.SurfaceRows * 36 + frame.Strokes.Sum(stroke => Math.Max(0, stroke.Points.Count - 1)) * 72);
        AppendSurfaceVertices(vertices, header, frame.SurfaceColors);
        AppendResidualVertices(vertices, header, frame.Residuals);

        foreach (StrokePrimitive stroke in frame.Strokes)
        {
            AppendStrokeVertices(vertices, header.Width, header.Height, stroke, true);
        }

        foreach (StrokePrimitive stroke in frame.Strokes)
        {
            AppendStrokeVertices(vertices, header.Width, header.Height, stroke, false);
        }

        return new StrokeGpuFrameData(vertices.ToArray());
    }

    private static void AppendSurfaceVertices(List<float> vertices, StrokeHeader header, Color[] surfaceColors)
    {
        for (int yCell = 0; yCell < header.SurfaceRows; yCell++)
        {
            int y0 = yCell * header.SurfaceCellSize;
            int y1 = Math.Min(header.Height, y0 + header.SurfaceCellSize);
            for (int xCell = 0; xCell < header.SurfaceColumns; xCell++)
            {
                int x0 = xCell * header.SurfaceCellSize;
                int x1 = Math.Min(header.Width, x0 + header.SurfaceCellSize);
                Color color = surfaceColors[yCell * header.SurfaceColumns + xCell];
                AddStrokeRect(vertices, header.Width, header.Height, x0, y0, x1, y1, color, 1.0f);
            }
        }
    }

    private static void AppendResidualVertices(List<float> vertices, StrokeHeader header, List<StrokeResidualPatch> residuals)
    {
        foreach (StrokeResidualPatch residual in residuals)
        {
            AddStrokeRect(vertices, header.Width, header.Height, residual.X, residual.Y, residual.X + residual.Width, residual.Y + residual.Height, residual.Color, residual.Opacity / 255f);
        }
    }

    private static void AppendStrokeVertices(List<float> vertices, int width, int height, StrokePrimitive stroke, bool glowPass)
    {
        if (stroke.Points.Count < 2)
        {
            return;
        }

        float coreWidth = Math.Max(1f, stroke.Width / 4f);
        float drawWidth = glowPass ? coreWidth + stroke.Glow : coreWidth;
        if (drawWidth <= 0)
        {
            return;
        }

        float alpha = stroke.Opacity / 255f;
        if (glowPass)
        {
            alpha *= Math.Clamp(stroke.Glow / 28f, 0.08f, 0.32f);
        }

        Color color = glowPass ? BoostStrokeGlowColor(stroke.Color, stroke.Intensity) : stroke.Color;
        for (int i = 1; i < stroke.Points.Count; i++)
        {
            AddStrokeSegment(vertices, width, height, stroke.Points[i - 1], stroke.Points[i], color, drawWidth, alpha);
        }
    }

    private static void AddStrokeSegment(List<float> vertices, int width, int height, Point a, Point b, Color color, float strokeWidth, float alpha)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
        {
            return;
        }

        float nx = -dy / length * strokeWidth * 0.5f;
        float ny = dx / length * strokeWidth * 0.5f;
        AddStrokeQuad(
            vertices,
            width,
            height,
            a.X + nx,
            a.Y + ny,
            b.X + nx,
            b.Y + ny,
            b.X - nx,
            b.Y - ny,
            a.X - nx,
            a.Y - ny,
            color,
            alpha);
    }

    private static void AddStrokeRect(List<float> vertices, int width, int height, float x0, float y0, float x1, float y1, Color color, float alpha)
    {
        AddStrokeQuad(vertices, width, height, x0, y0, x1, y0, x1, y1, x0, y1, color, alpha);
    }

    private static void AddStrokeQuad(List<float> vertices, int width, int height, float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, Color color, float alpha)
    {
        AddStrokeVertex(vertices, x0, y0, color, alpha, width, height);
        AddStrokeVertex(vertices, x1, y1, color, alpha, width, height);
        AddStrokeVertex(vertices, x2, y2, color, alpha, width, height);
        AddStrokeVertex(vertices, x0, y0, color, alpha, width, height);
        AddStrokeVertex(vertices, x2, y2, color, alpha, width, height);
        AddStrokeVertex(vertices, x3, y3, color, alpha, width, height);
    }

    private static void AddStrokeVertex(List<float> vertices, float x, float y, Color color, float alpha, int width, int height)
    {
        float nx = x / Math.Max(1, width) * 2f - 1f;
        float ny = 1f - y / Math.Max(1, height) * 2f;
        vertices.Add(nx);
        vertices.Add(ny);
        vertices.Add(color.R / 255f);
        vertices.Add(color.G / 255f);
        vertices.Add(color.B / 255f);
        vertices.Add(Math.Clamp(alpha, 0f, 1f));
    }

    private static Stream OpenStrokeWriteStream(string path, CompressionLevel compressionLevel)
    {
        FileStream fileStream = File.Create(path);
        return new BrotliStream(fileStream, compressionLevel);
    }

    private static Stream OpenStrokeReadStream(string path)
    {
        FileStream fileStream = File.OpenRead(path);
        return new BrotliStream(fileStream, CompressionMode.Decompress);
    }

    private static int StrokeSurfaceCellSize(int width, int height, int quality, int surfaceDetail)
    {
        double longEdgeScale = Math.Clamp(Math.Max(width, height) / 1080.0, 0.75, 2.0);
        double size = (50 - surfaceDetail * 0.30 - quality * 0.11) * longEdgeScale;
        return Math.Clamp((int)Math.Round(size), 12, 64);
    }

    private static int SurfaceDeltaThreshold(int quality, int surfaceDetail)
    {
        return Math.Clamp(20 - quality / 9 - surfaceDetail / 12, 5, 18);
    }

    private static int StrokeMinComponentPixels(int width, int height, int quality, int strokeDensity)
    {
        double scale = Math.Sqrt(width * height / (1280.0 * 720.0));
        return Math.Clamp((int)Math.Round((18 - quality * 0.08 - strokeDensity * 0.05) * scale), 4, 22);
    }

    private static double StrokeMinLength(int width, int height, int quality, int strokeDensity)
    {
        double diagonal = Math.Sqrt(width * width + height * height);
        return Math.Clamp(diagonal * (0.008 - quality * 0.000025 - strokeDensity * 0.000018), 5, 28);
    }

    private static double StrokeSimplifyForQuality(int quality, int strokeDensity)
    {
        return Math.Clamp(3.0 - quality * 0.018 - strokeDensity * 0.006, 0.55, 3.0);
    }

    private static int MaxStrokesForFrame(int width, int height, int strokeDensity)
    {
        double megapixels = width * height / 1_000_000.0;
        return Math.Clamp((int)Math.Round(120 + strokeDensity * 8 + megapixels * 90), 80, 1600);
    }

    private static int ResidualErrorThreshold(int quality, int residualStrength)
    {
        return Math.Clamp(46 - quality / 5 - residualStrength / 3, 12, 42);
    }

    private static int MaxResidualPatches(int residualStrength, int width, int height)
    {
        double megapixels = width * height / 1_000_000.0;
        return Math.Clamp((int)Math.Round(residualStrength * (2.8 + megapixels)), 0, 900);
    }

    private static double StrokePriority(StrokePrimitive stroke)
    {
        return stroke.Length * (0.6 + stroke.Intensity / 255.0) * (stroke.Opacity / 255.0);
    }

    private static double StrokeEndpointDistance(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return double.MaxValue;
        }

        Point a0 = a[0];
        Point a1 = a[^1];
        Point b0 = b[0];
        Point b1 = b[^1];
        double forward = Distance(a0, b0) + Distance(a1, b1);
        double reverse = Distance(a0, b1) + Distance(a1, b0);
        return Math.Min(forward, reverse) * 0.5;
    }

    private static List<Point> SimplifyStrokePath(List<Point> points, double epsilon)
    {
        if (points.Count <= 2 || epsilon <= 0)
        {
            return points.ToList();
        }

        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifyStrokePathRecursive(points, 0, points.Count - 1, epsilon * epsilon, keep);

        List<Point> simplified = new();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                simplified.Add(points[i]);
            }
        }

        return simplified.Count >= 2 ? simplified : points.Take(2).ToList();
    }

    private static void SimplifyStrokePathRecursive(List<Point> points, int start, int end, double epsilonSquared, bool[] keep)
    {
        if (end <= start + 1)
        {
            return;
        }

        double bestDistance = 0;
        int bestIndex = -1;
        Point a = points[start];
        Point b = points[end];
        for (int i = start + 1; i < end; i++)
        {
            double distance = DistanceToSegmentSquared(points[i], a, b);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0 && bestDistance > epsilonSquared)
        {
            keep[bestIndex] = true;
            SimplifyStrokePathRecursive(points, start, bestIndex, epsilonSquared, keep);
            SimplifyStrokePathRecursive(points, bestIndex, end, epsilonSquared, keep);
        }
    }

    private static double StrokePathLength(IReadOnlyList<Point> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    private static Color BoostStrokeGlowColor(Color color, byte intensity)
    {
        double boost = 1.08 + intensity / 255.0 * 0.28;
        return Color.FromArgb(
            ClampByte((int)Math.Round(color.R * boost)),
            ClampByte((int)Math.Round(color.G * boost)),
            ClampByte((int)Math.Round(color.B * boost)));
    }

    private static Color BlendColor(Color oldColor, Color newColor, double newWeight)
    {
        double oldWeight = 1.0 - newWeight;
        return Color.FromArgb(
            ClampByte((int)Math.Round(oldColor.R * oldWeight + newColor.R * newWeight)),
            ClampByte((int)Math.Round(oldColor.G * oldWeight + newColor.G * newWeight)),
            ClampByte((int)Math.Round(oldColor.B * oldWeight + newColor.B * newWeight)));
    }

    private static byte BlendByte(byte oldValue, byte newValue, double newWeight)
    {
        return (byte)Math.Clamp((int)Math.Round(oldValue * (1.0 - newWeight) + newValue * newWeight), 0, 255);
    }

    private static byte BlendByte(byte oldValue, int newValue, double alpha)
    {
        return (byte)Math.Clamp((int)Math.Round(oldValue * (1.0 - alpha) + newValue * alpha), 0, 255);
    }

    private static int DivRoundUp(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }

    private static int NeighborDx(int neighborIndex)
    {
        return neighborIndex switch
        {
            0 or 3 or 5 => -1,
            2 or 4 or 7 => 1,
            _ => 0
        };
    }

    private static int NeighborDy(int neighborIndex)
    {
        return neighborIndex switch
        {
            0 or 1 or 2 => -1,
            5 or 6 or 7 => 1,
            _ => 0
        };
    }

    private sealed class StrokeFrameWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly BinaryWriter _writer;

        public StrokeFrameWriter(string path, CompressionLevel compressionLevel)
        {
            _stream = OpenStrokeWriteStream(path, compressionLevel);
            _writer = new BinaryWriter(_stream, Encoding.UTF8);
        }

        public void WriteHeader(StrokeHeader header)
        {
            _writer.Write(StrokeMagic);
            _writer.Write(StrokeFileVersion);
            WriteVarInt(_writer, header.Width);
            WriteVarInt(_writer, header.Height);
            _writer.Write(header.Fps);
            WriteVarInt(_writer, header.Quality);
            WriteVarInt(_writer, header.StrokeDensity);
            WriteVarInt(_writer, header.SurfaceDetail);
            WriteVarInt(_writer, header.ResidualStrength);
            WriteVarInt(_writer, header.Glow);
            WriteVarInt(_writer, header.KeyframeInterval);
            WriteVarInt(_writer, header.SurfaceCellSize);
            WriteVarInt(_writer, header.SurfaceColumns);
            WriteVarInt(_writer, header.SurfaceRows);
        }

        public void WriteFrame(StrokeFrame frame)
        {
            _writer.Write(StrokeFrameMarker);
            WriteVarInt(_writer, frame.FrameNumber);
            _writer.Write((byte)(frame.IsKeyframe ? 1 : 0));

            WriteVarInt(_writer, frame.SurfaceChanges.Count);
            foreach (StrokeSurfaceChange change in frame.SurfaceChanges)
            {
                WriteVarInt(_writer, change.Index);
                WriteStrokeColor(_writer, change.Color);
            }

            WriteVarInt(_writer, frame.Strokes.Count);
            foreach (StrokePrimitive stroke in frame.Strokes)
            {
                WriteVarInt(_writer, stroke.Id);
                WriteStrokeColor(_writer, stroke.Color);
                _writer.Write(stroke.Intensity);
                _writer.Write(stroke.Width);
                _writer.Write(stroke.Glow);
                _writer.Write(stroke.Opacity);
                WriteVarInt(_writer, stroke.Points.Count);
                foreach (Point point in stroke.Points)
                {
                    WriteVarInt(_writer, point.X);
                    WriteVarInt(_writer, point.Y);
                }
            }

            WriteVarInt(_writer, frame.Residuals.Count);
            foreach (StrokeResidualPatch residual in frame.Residuals)
            {
                WriteVarInt(_writer, residual.X);
                WriteVarInt(_writer, residual.Y);
                WriteVarInt(_writer, residual.Width);
                WriteVarInt(_writer, residual.Height);
                WriteStrokeColor(_writer, residual.Color);
                _writer.Write(residual.Opacity);
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class StrokeFrameReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly BinaryReader _reader;
        private readonly Color[] _surfaceColors;

        public StrokeFrameReader(string path)
        {
            _stream = OpenStrokeReadStream(path);
            _reader = new BinaryReader(_stream, Encoding.UTF8);
            Header = ReadHeader();
            _surfaceColors = Enumerable.Repeat(Color.Black, Header.SurfaceColumns * Header.SurfaceRows).ToArray();
        }

        public StrokeHeader Header { get; }

        public DecodedStrokeFrame? ReadNextFrame()
        {
            byte marker;
            try
            {
                marker = _reader.ReadByte();
            }
            catch (EndOfStreamException)
            {
                return null;
            }

            if (marker != StrokeFrameMarker)
            {
                throw new InvalidDataException($"Invalid LVFS frame marker: 0x{marker:X2}");
            }

            int frameNumber = ReadVarInt(_reader);
            bool isKeyframe = _reader.ReadByte() != 0;

            int surfaceChangeCount = ReadVarInt(_reader);
            for (int i = 0; i < surfaceChangeCount; i++)
            {
                int index = ReadVarInt(_reader);
                if ((uint)index >= (uint)_surfaceColors.Length)
                {
                    throw new InvalidDataException($"LVFS surface cell index out of range: {index}");
                }

                _surfaceColors[index] = ReadStrokeColor(_reader);
            }

            int strokeCount = ReadVarInt(_reader);
            List<StrokePrimitive> strokes = new(strokeCount);
            for (int i = 0; i < strokeCount; i++)
            {
                int id = ReadVarInt(_reader);
                Color color = ReadStrokeColor(_reader);
                byte intensity = _reader.ReadByte();
                byte width = _reader.ReadByte();
                byte glow = _reader.ReadByte();
                byte opacity = _reader.ReadByte();
                int pointCount = ReadVarInt(_reader);
                List<Point> points = new(pointCount);
                for (int p = 0; p < pointCount; p++)
                {
                    points.Add(new Point(ReadVarInt(_reader), ReadVarInt(_reader)));
                }

                strokes.Add(new StrokePrimitive(id, points, color, intensity, width, glow, opacity, BoundsFor(points), AveragePoint(points), StrokePathLength(points), 0));
            }

            int residualCount = ReadVarInt(_reader);
            List<StrokeResidualPatch> residuals = new(residualCount);
            for (int i = 0; i < residualCount; i++)
            {
                int x = ReadVarInt(_reader);
                int y = ReadVarInt(_reader);
                int width = ReadVarInt(_reader);
                int height = ReadVarInt(_reader);
                Color color = ReadStrokeColor(_reader);
                byte opacity = _reader.ReadByte();
                residuals.Add(new StrokeResidualPatch(x, y, width, height, color, opacity));
            }

            return new DecodedStrokeFrame(frameNumber, isKeyframe, (Color[])_surfaceColors.Clone(), strokes, residuals);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }

        private StrokeHeader ReadHeader()
        {
            byte[] magic = _reader.ReadBytes(StrokeMagic.Length);
            if (magic.Length != StrokeMagic.Length || !magic.SequenceEqual(StrokeMagic))
            {
                throw new InvalidDataException("Unknown LVFS binary format.");
            }

            byte version = _reader.ReadByte();
            if (version != StrokeFileVersion)
            {
                throw new InvalidDataException($"Unsupported LVFS version: {version}");
            }

            int width = ReadVarInt(_reader);
            int height = ReadVarInt(_reader);
            double fps = _reader.ReadDouble();
            int quality = ReadVarInt(_reader);
            int strokeDensity = ReadVarInt(_reader);
            int surfaceDetail = ReadVarInt(_reader);
            int residualStrength = ReadVarInt(_reader);
            int glow = ReadVarInt(_reader);
            int keyframeInterval = ReadVarInt(_reader);
            int surfaceCellSize = ReadVarInt(_reader);
            int surfaceColumns = ReadVarInt(_reader);
            int surfaceRows = ReadVarInt(_reader);
            return new StrokeHeader(width, height, fps, quality, strokeDensity, surfaceDetail, residualStrength, glow, keyframeInterval, surfaceCellSize, surfaceColumns, surfaceRows);
        }
    }

    private sealed class StrokePlaybackWindow : GameWindow
    {
        private const int FloatsPerVertex = 6;
        private readonly StrokeHeader _header;
        private readonly BlockingCollection<StrokeGpuFrameData> _frames;
        private readonly double _frameMs;
        private readonly Stopwatch _clock = new();
        private int _program;
        private int _vertexArray;
        private int _vertexBuffer;
        private StrokeGpuFrameData? _currentFrame;

        public StrokePlaybackWindow(StrokeHeader header, BlockingCollection<StrokeGpuFrameData> frames, string fileName)
            : base(CreateGameWindowSettings(), CreateNativeWindowSettings(header, fileName))
        {
            _header = header;
            _frames = frames;
            _frameMs = 1000.0 / Math.Max(1, header.Fps);
            VSync = VSyncMode.On;
        }

        public int DisplayedFrames { get; private set; }

        protected override void OnLoad()
        {
            base.OnLoad();
            _program = CreateStrokeShaderProgram();
            _vertexArray = GL.GenVertexArray();
            _vertexBuffer = GL.GenBuffer();

            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 2 * sizeof(float));
            GL.BindVertexArray(0);

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            UpdateStrokeViewport();
            _clock.Start();
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            UpdateStrokeViewport();
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            base.OnUpdateFrame(args);
            if (KeyboardState.IsKeyDown(Keys.Escape) || KeyboardState.IsKeyDown(Keys.Q))
            {
                Close();
            }
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);
            TryAdvanceFrame();
            RenderCurrentFrame();
            SwapBuffers();
        }

        protected override void OnUnload()
        {
            base.OnUnload();
            if (_vertexBuffer != 0)
            {
                GL.DeleteBuffer(_vertexBuffer);
            }

            if (_vertexArray != 0)
            {
                GL.DeleteVertexArray(_vertexArray);
            }

            if (_program != 0)
            {
                GL.DeleteProgram(_program);
            }
        }

        private static GameWindowSettings CreateGameWindowSettings()
        {
            return new GameWindowSettings
            {
                UpdateFrequency = 120
            };
        }

        private static NativeWindowSettings CreateNativeWindowSettings(StrokeHeader header, string fileName)
        {
            int maxWidth = 1280;
            int maxHeight = 720;
            double scale = Math.Min(1.0, Math.Min(maxWidth / (double)Math.Max(1, header.Width), maxHeight / (double)Math.Max(1, header.Height)));
            return new NativeWindowSettings
            {
                ClientSize = new Vector2i(Math.Max(320, (int)Math.Round(header.Width * scale)), Math.Max(180, (int)Math.Round(header.Height * scale))),
                Title = $"LVFVF Stroke Player - {fileName}"
            };
        }

        private void TryAdvanceFrame()
        {
            double nextFrameTime = DisplayedFrames * _frameMs;
            if (_currentFrame is not null && _clock.Elapsed.TotalMilliseconds < nextFrameTime)
            {
                return;
            }

            if (_frames.TryTake(out StrokeGpuFrameData? frame, _currentFrame is null ? 100 : 0))
            {
                _currentFrame = frame;
                UploadFrame(frame);
                DisplayedFrames++;
                return;
            }

            if (_frames.IsCompleted && _clock.Elapsed.TotalMilliseconds >= nextFrameTime + _frameMs)
            {
                Close();
            }
        }

        private void RenderCurrentFrame()
        {
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            if (_currentFrame is null || _currentFrame.VertexCount == 0)
            {
                return;
            }

            GL.UseProgram(_program);
            GL.BindVertexArray(_vertexArray);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _currentFrame.VertexCount);
            GL.BindVertexArray(0);
        }

        private void UploadFrame(StrokeGpuFrameData frame)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, frame.Vertices.Length * sizeof(float), frame.Vertices, BufferUsageHint.StreamDraw);
        }

        private void UpdateStrokeViewport()
        {
            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        }

        private static int CreateStrokeShaderProgram()
        {
            const string vertexShader = """
                #version 330 core
                layout (location = 0) in vec2 aPosition;
                layout (location = 1) in vec4 aColor;
                out vec4 vColor;
                void main()
                {
                    gl_Position = vec4(aPosition, 0.0, 1.0);
                    vColor = aColor;
                }
                """;

            const string fragmentShader = """
                #version 330 core
                in vec4 vColor;
                out vec4 FragColor;
                void main()
                {
                    FragColor = vColor;
                }
                """;

            int vertex = CompileStrokeShader(ShaderType.VertexShader, vertexShader);
            int fragment = CompileStrokeShader(ShaderType.FragmentShader, fragmentShader);
            int program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
            if (success == 0)
            {
                string log = GL.GetProgramInfoLog(program);
                throw new InvalidOperationException($"Stroke shader link failed: {log}");
            }

            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return program;
        }

        private static int CompileStrokeShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
            {
                string log = GL.GetShaderInfoLog(shader);
                throw new InvalidOperationException($"{type} compile failed: {log}");
            }

            return shader;
        }
    }

    private sealed class StrokeEncodeProfiler
    {
        private readonly long[] _ticks = new long[Enum.GetValues<StrokeEncodeStage>().Length];

        public void Add(StrokeEncodeStage stage, long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Add(ref _ticks[(int)stage], elapsed);
        }

        public void Print(int frameCount)
        {
            double totalSeconds = _ticks.Sum() / (double)Stopwatch.Frequency;
            Console.WriteLine();
            Console.WriteLine("Stroke profile:");
            Console.WriteLine($"  Measured stage time: {FormatDouble(totalSeconds)}s across {frameCount} frames");
            foreach (StrokeEncodeStage stage in Enum.GetValues<StrokeEncodeStage>())
            {
                double seconds = _ticks[(int)stage] / (double)Stopwatch.Frequency;
                double perFrameMs = frameCount <= 0 ? 0 : seconds * 1000 / frameCount;
                double percent = totalSeconds <= 0 ? 0 : seconds / totalSeconds * 100;
                Console.WriteLine($"  {StrokeStageName(stage),-16} {FormatDouble(seconds),7}s  {FormatDouble(perFrameMs),7} ms/frame  {percent,5:0.0}%");
            }
        }

        private static string StrokeStageName(StrokeEncodeStage stage)
        {
            return stage switch
            {
                StrokeEncodeStage.Surfaces => "surfaces",
                StrokeEncodeStage.Analysis => "analysis",
                StrokeEncodeStage.Edges => "edges",
                StrokeEncodeStage.LinkStrokes => "link strokes",
                StrokeEncodeStage.TrackStrokes => "track strokes",
                StrokeEncodeStage.Residuals => "residuals",
                StrokeEncodeStage.Write => "write",
                StrokeEncodeStage.FrameTotal => "frame total",
                _ => stage.ToString()
            };
        }
    }

    private static void WriteStrokeColor(BinaryWriter writer, Color color)
    {
        writer.Write(color.R);
        writer.Write(color.G);
        writer.Write(color.B);
    }

    private static Color ReadStrokeColor(BinaryReader reader)
    {
        byte r = reader.ReadByte();
        byte g = reader.ReadByte();
        byte b = reader.ReadByte();
        return Color.FromArgb(r, g, b);
    }

    private enum StrokeEncodeStage
    {
        Surfaces,
        Analysis,
        Edges,
        LinkStrokes,
        TrackStrokes,
        Residuals,
        Write,
        FrameTotal
    }

    private sealed record StrokeEncodeOptions(int Quality, int StrokeDensity, int SurfaceDetail, int ResidualStrength, int Glow, int KeyframeInterval, int PipelineDepth, CompressionLevel CompressionLevel, bool Profile, int MaxFrames);
    private sealed record StrokeHeader(int Width, int Height, double Fps, int Quality, int StrokeDensity, int SurfaceDetail, int ResidualStrength, int Glow, int KeyframeInterval, int SurfaceCellSize, int SurfaceColumns, int SurfaceRows);
    private sealed record StrokeFrame(int FrameNumber, bool IsKeyframe, List<StrokeSurfaceChange> SurfaceChanges, List<StrokePrimitive> Strokes, List<StrokeResidualPatch> Residuals);
    private sealed record DecodedStrokeFrame(int FrameNumber, bool IsKeyframe, Color[] SurfaceColors, List<StrokePrimitive> Strokes, List<StrokeResidualPatch> Residuals);
    private sealed record StrokeSurfaceChange(int Index, Color Color);
    private sealed record StrokeResidualPatch(int X, int Y, int Width, int Height, Color Color, byte Opacity);
    private sealed record StrokeResidualCandidate(double Error, StrokeResidualPatch Patch);
    private sealed record GradientField(byte[] Magnitude, short[] Gx, short[] Gy);
    private sealed record StrokeGpuFrameData(float[] Vertices)
    {
        public int VertexCount => Vertices.Length / 6;
    }

    private sealed class StrokePrimitive
    {
        public StrokePrimitive(int id, List<Point> points, Color color, byte intensity, byte width, byte glow, byte opacity, Rectangle bounds, PointF center, double length, int misses)
        {
            Id = id;
            Points = points;
            Color = color;
            Intensity = intensity;
            Width = width;
            Glow = glow;
            Opacity = opacity;
            Bounds = bounds;
            Center = center;
            Length = length;
            Misses = misses;
        }

        public int Id { get; set; }
        public List<Point> Points { get; }
        public Color Color { get; set; }
        public byte Intensity { get; set; }
        public byte Width { get; set; }
        public byte Glow { get; set; }
        public byte Opacity { get; set; }
        public Rectangle Bounds { get; set; }
        public PointF Center { get; set; }
        public double Length { get; set; }
        public int Misses { get; set; }
    }

    private sealed record TrackedStroke(int Id, List<Point> Points, Color Color, byte Intensity, byte Width, byte Glow, byte Opacity, Rectangle Bounds, PointF Center, double Length, int Misses);

    private sealed class StrokeEncodeState
    {
        public StrokeEncodeState(StrokeHeader header)
        {
            Header = header;
        }

        public StrokeHeader Header { get; }
        public Color[]? SurfaceColors { get; set; }
        public List<TrackedStroke> PreviousStrokes { get; set; } = new();
        public int NextStrokeId { get; set; } = 1;
    }
}
