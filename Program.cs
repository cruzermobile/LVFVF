using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using ILGPU;
using ILGPU.Runtime;
using LibTessDotNet;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    private const int PlaybackBufferSize = 96;
    private const int DefaultQuality = 82;
    private const int DefaultDarkFilter = 65;
    private const int DefaultPatchDetail = 35;
    private const int DefaultObjectFocus = 45;
    private const int DefaultCorrectionStrength = 100;
    private const double DefaultEdgeSensitivity = 80;
    private const double DefaultEdgeSimplify = 1.75;
    private const int MaxKMeansSamples = 14000;
    private const byte BinaryRegionVersion = 2;
    private const byte BinaryFrameMarker = 0xF2;
    private static readonly byte[] BinaryRegionMagic = Encoding.ASCII.GetBytes("LVFB2");

    static int Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? RunInteractive() : RunCommand(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunCommand(string[] args)
    {
        string command = args[0].Trim().ToLowerInvariant();
        List<string> remaining = args.Skip(1).ToList();

        switch (command)
        {
            case "convert":
            case "encode":
                return ConvertCommand(remaining);
            case "convert-edges":
            case "encode-edges":
                return ConvertEdgesCommand(remaining);
            case "play":
                return PlayCommand(remaining);
            case "play-gpu":
            case "gpu-play":
                return PlayGpuCommand(remaining);
            case "info":
            case "inspect":
                return InfoCommand(remaining);
            case "gpu-info":
            case "accel-info":
                return GpuInfoCommand();
            case "help":
            case "-h":
            case "--help":
                PrintUsage();
                return 0;
            default:
                Console.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static int RunInteractive()
    {
        Console.WriteLine("LVFVF - lightweight vector frame video");
        Console.WriteLine("1) Play an LVF file");
        Console.WriteLine("2) Convert a video to LVFVF2 regions");
        Console.WriteLine("3) Inspect an LVF file");
        Console.Write("Choice: ");

        string choice = Console.ReadLine()?.Trim() ?? "";
        if (choice == "1")
        {
            string lvfPath = ReadRequired("LVF path: ");
            PlayLVF(lvfPath);
            return 0;
        }

        if (choice == "2")
        {
            string videoPath = ReadRequired("Video path: ");
            string defaultOutput = Path.ChangeExtension(videoPath, ".lvfb") ?? $"{videoPath}.lvfb";
            Console.Write($"Output path [{defaultOutput}]: ");
            string? outputInput = Console.ReadLine();
            string outputPath = string.IsNullOrWhiteSpace(outputInput) ? defaultOutput : outputInput.Trim();

            int quality = ReadInt($"Quality 1-100 [{DefaultQuality}]: ", DefaultQuality, 1, 100);
            int paletteSize = ReadInt($"Palette regions/colors [{PaletteSizeForQuality(quality)}]: ", PaletteSizeForQuality(quality), 2, 128);

            using AccelerationOptions acceleration = CreateAccelerationOptions(AccelerationMode.Auto);
            ProcessVideoRegions(videoPath, outputPath, quality, paletteSize, DefaultDarkFilter, DefaultPatchDetail, DefaultObjectFocus, DefaultCorrectionStrength, acceleration, CompressionLevel.Optimal, RegionTracerMode.OpenCv, 1, false);
            return 0;
        }

        if (choice == "3")
        {
            string lvfPath = ReadRequired("LVF path: ");
            InspectLVF(lvfPath);
            return 0;
        }

        Console.WriteLine("Invalid choice.");
        return 1;
    }

    private static int ConvertCommand(List<string> args)
    {
        bool profile = TakeFlag(args, "--profile");
        CompressionLevel compressionLevel = TakeCompressionOption(args);
        RegionTracerMode tracerMode = TakeTracerOption(args);
        int pipelineDepth = TakeIntOption(args, 1, 1, Math.Max(1, Environment.ProcessorCount / 2), "--pipeline", "--parallel-frames");
        int quality = TakeIntOption(args, DefaultQuality, 1, 100, "--quality", "-q");
        int paletteSize = TakeIntOption(args, PaletteSizeForQuality(quality), 2, 128, "--palette", "--colors");
        int darkFilter = TakeIntOption(args, DefaultDarkFilter, 0, 100, "--dark-filter", "--despeckle");
        int patchDetail = TakeIntOption(args, DefaultPatchDetail, 0, 100, "--patch-detail", "--detail");
        int objectFocus = TakeIntOption(args, DefaultObjectFocus, 0, 100, "--object-focus", "--foreground-focus", "--fg-focus");
        int correctionStrength = TakeIntOption(args, DefaultCorrectionStrength, 0, 100, "--corrections", "--residuals", "--correction-strength");
        using AccelerationOptions acceleration = CreateAccelerationOptions(TakeAccelerationOption(args));

        if (args.Count is < 1 or > 2)
        {
            PrintUsage();
            return 1;
        }

        string input = args[0];
        string output = args.Count == 2 ? args[1] : Path.ChangeExtension(input, ".lvfb") ?? $"{input}.lvfb";
        ProcessVideoRegions(input, output, quality, paletteSize, darkFilter, patchDetail, objectFocus, correctionStrength, acceleration, compressionLevel, tracerMode, pipelineDepth, profile);
        return 0;
    }

    private static int ConvertEdgesCommand(List<string> args)
    {
        CompressionLevel compressionLevel = TakeCompressionOption(args);
        bool highQuality = TakeFlag(args, "--high-quality", "--hq");
        double sensitivity = TakeDoubleOption(args, DefaultEdgeSensitivity, "--sensitivity", "-s");
        double simplify = TakeDoubleOption(args, DefaultEdgeSimplify, "--simplify");
        using AccelerationOptions acceleration = CreateAccelerationOptions(TakeAccelerationOption(args));

        if (args.Count is < 1 or > 2)
        {
            PrintUsage();
            return 1;
        }

        string input = args[0];
        string output = args.Count == 2 ? args[1] : Path.ChangeExtension(input, ".lvfz") ?? $"{input}.lvfz";
        ProcessVideoEdges(input, output, sensitivity, highQuality, simplify, acceleration, compressionLevel);
        return 0;
    }

    private static int PlayCommand(List<string> args)
    {
        bool useGpu = TakeFlag(args, "--gpu", "--renderer-gpu");
        string? renderer = TakeStringOption(args, "--renderer");
        if (renderer is not null)
        {
            useGpu = renderer.Equals("gpu", StringComparison.OrdinalIgnoreCase) ||
                renderer.Equals("opengl", StringComparison.OrdinalIgnoreCase);
        }

        if (args.Count != 1)
        {
            PrintUsage();
            return 1;
        }

        if (useGpu)
        {
            PlayGpuLVF(args[0]);
        }
        else
        {
            PlayLVF(args[0]);
        }

        return 0;
    }

    private static int PlayGpuCommand(List<string> args)
    {
        if (args.Count != 1)
        {
            PrintUsage();
            return 1;
        }

        PlayGpuLVF(args[0]);
        return 0;
    }

    private static int InfoCommand(List<string> args)
    {
        if (args.Count != 1)
        {
            PrintUsage();
            return 1;
        }

        InspectLVF(args[0]);
        return 0;
    }

    private static int GpuInfoCommand()
    {
        Console.WriteLine("Acceleration:");
        Console.WriteLine($"  CUDA available: {CudaLabeler.IsAvailable()}");
        Console.WriteLine($"  OpenCL available: {SafeHaveOpenCl()}");
        Console.WriteLine($"  OpenCL GPU device: {SafeHaveOpenClGpu()}");
        Console.WriteLine($"  OpenCL enabled: {SafeUseOpenCl()}");

        string cudaSummary = CudaLabeler.GetSummary();
        if (!string.IsNullOrWhiteSpace(cudaSummary))
        {
            Console.WriteLine();
            Console.WriteLine(cudaSummary.Trim());
        }

        string summary = SafeOpenClSummary();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            Console.WriteLine();
            Console.WriteLine(summary.Trim());
        }

        Console.WriteLine();
        Console.WriteLine("FFmpeg hardware decode is attempted with --accel auto or --accel ffmpeg.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  LVFVF convert <input-video> [output.lvfb] [--quality 82] [--palette 36] [--dark-filter 65] [--patch-detail 35] [--object-focus 45] [--corrections 100] [--tracer opencv|merged-fast|merged|custom] [--pipeline 1] [--accel auto|cpu|cuda|opencl|hybrid|ffmpeg] [--compression optimal|fast|smallest] [--profile]");
        Console.WriteLine("  LVFVF play <file.lvf|file.lvfz|file.lvfb> [--renderer cpu|gpu]");
        Console.WriteLine("  LVFVF play-gpu <file.lvfb>");
        Console.WriteLine("  LVFVF info <file.lvf|file.lvfz|file.lvfb>");
        Console.WriteLine("  LVFVF gpu-info");
        Console.WriteLine();
        Console.WriteLine("Experimental legacy edge mode:");
        Console.WriteLine("  LVFVF convert-edges <input-video> [output.lvfz] [--sensitivity 80] [--simplify 1.75] [--accel auto|cpu|cuda|opencl|hybrid|ffmpeg] [--compression optimal|fast|smallest]");
        Console.WriteLine();
        Console.WriteLine("LVFVF2 stores frames as filled traced regions with stable-ish shape IDs.");
        Console.WriteLine(".lvfb files are Brotli-compressed binary LVFVF2; .lvfz is compressed LVF text; .lvf remains raw text.");
        Console.WriteLine("--accel auto uses CUDA label assignment when available, otherwise OpenCL preprocessing, plus FFmpeg hardware decode.");
    }

    private static void PlayLVF(string lvfPath)
    {
        if (IsBinaryLvfPath(lvfPath))
        {
            PlayBinaryLVF(lvfPath);
            return;
        }

        using Stream lvfStream = OpenLvfReadStream(lvfPath);
        using StreamReader reader = new(lvfStream, Encoding.UTF8, true);
        LvfHeader header = ReadHeader(reader);
        if (header.Format == LvfFormat.Legacy)
        {
            Console.WriteLine("Legacy LVF detected. It has no frame markers, so each line will play as one frame.");
        }

        using BlockingCollection<Mat> queue = new(PlaybackBufferSize);
        Exception? producerError = null;

        Task producer = Task.Run(() =>
        {
            try
            {
                while (ReadNextFrame(reader, header) is { } vectorFrame)
                {
                    Mat frame = RenderFrame(vectorFrame, header.Width, header.Height);
                    queue.Add(frame);
                }
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

        Console.WriteLine($"Playing {Path.GetFileName(lvfPath)} at {FormatDouble(header.Fps)} fps ({header.Width}x{header.Height}). Press Q or Esc to stop.");

        int frameCount = 0;
        double frameMs = 1000.0 / Math.Max(header.Fps, 1);
        Stopwatch playbackClock = Stopwatch.StartNew();

        foreach (Mat frame in queue.GetConsumingEnumerable())
        {
            frameCount++;
            CvInvoke.Imshow("LVFVF Playback", frame);
            frame.Dispose();

            double targetMs = frameCount * frameMs;
            int wait = Math.Max(1, (int)Math.Round(targetMs - playbackClock.Elapsed.TotalMilliseconds));
            int key = CvInvoke.WaitKey(wait);
            if (key is 27 or 'q' or 'Q')
            {
                break;
            }

            if (frameCount % 30 == 0)
            {
                double bufferFill = (double)queue.Count / PlaybackBufferSize * 100;
                Console.WriteLine($"Frame {frameCount} | Buffer {bufferFill:F0}%");
            }
        }

        producer.Wait();
        if (producerError is not null)
        {
            throw new InvalidDataException($"Playback stopped while reading LVF data: {producerError.Message}", producerError);
        }

        CvInvoke.DestroyAllWindows();
        Console.WriteLine($"Playback finished after {frameCount} frames.");
    }

    private static void PlayBinaryLVF(string lvfPath)
    {
        using Stream lvfStream = OpenLvfReadStream(lvfPath);
        using BinaryReader reader = new(lvfStream, Encoding.UTF8);
        LvfHeader header = ReadBinaryHeader(reader);

        using BlockingCollection<Mat> queue = new(PlaybackBufferSize);
        Exception? producerError = null;

        Task producer = Task.Run(() =>
        {
            try
            {
                while (ReadNextBinaryRegionFrame(reader, header.BinaryVersion) is { } vectorFrame)
                {
                    Mat frame = RenderFrame(vectorFrame, header.Width, header.Height);
                    queue.Add(frame);
                }
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

        Console.WriteLine($"Playing {Path.GetFileName(lvfPath)} at {FormatDouble(header.Fps)} fps ({header.Width}x{header.Height}). Press Q or Esc to stop.");

        int frameCount = 0;
        double frameMs = 1000.0 / Math.Max(header.Fps, 1);
        Stopwatch playbackClock = Stopwatch.StartNew();

        foreach (Mat frame in queue.GetConsumingEnumerable())
        {
            frameCount++;
            CvInvoke.Imshow("LVFVF Playback", frame);
            frame.Dispose();

            double targetMs = frameCount * frameMs;
            int wait = Math.Max(1, (int)Math.Round(targetMs - playbackClock.Elapsed.TotalMilliseconds));
            int key = CvInvoke.WaitKey(wait);
            if (key is 27 or 'q' or 'Q')
            {
                break;
            }

            if (frameCount % 30 == 0)
            {
                double bufferFill = (double)queue.Count / PlaybackBufferSize * 100;
                Console.WriteLine($"Frame {frameCount} | Buffer {bufferFill:F0}%");
            }
        }

        producer.Wait();
        if (producerError is not null)
        {
            throw new InvalidDataException($"Playback stopped while reading LVF data: {producerError.Message}", producerError);
        }

        CvInvoke.DestroyAllWindows();
        Console.WriteLine($"Playback finished after {frameCount} frames.");
    }

    private static void PlayGpuLVF(string lvfPath)
    {
        if (!IsBinaryLvfPath(lvfPath))
        {
            throw new InvalidOperationException("GPU playback currently supports compressed binary LVFVF2 files (.lvfb). Use normal play for text or legacy files.");
        }

        using Stream lvfStream = OpenLvfReadStream(lvfPath);
        using BinaryReader reader = new(lvfStream, Encoding.UTF8);
        LvfHeader header = ReadBinaryHeader(reader);

        using BlockingCollection<GpuFrameData> queue = new(PlaybackBufferSize);
        using CancellationTokenSource cancellation = new();
        Exception? producerError = null;

        Task producer = Task.Run(() =>
        {
            try
            {
                while (ReadNextBinaryRegionFrame(reader, header.BinaryVersion) is { } vectorFrame)
                {
                    queue.Add(BuildGpuFrame(vectorFrame, header.Width, header.Height), cancellation.Token);
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

        using GpuPlaybackWindow window = new(header, queue, Path.GetFileName(lvfPath));
        window.Run();

        cancellation.Cancel();
        producer.Wait();
        if (producerError is not null)
        {
            throw new InvalidDataException($"GPU playback stopped while reading LVF data: {producerError.Message}", producerError);
        }

        Console.WriteLine($"GPU playback finished after {window.DisplayedFrames} frames.");
    }

    private static void InspectLVF(string lvfPath)
    {
        if (IsBinaryLvfPath(lvfPath))
        {
            InspectBinaryLVF(lvfPath);
            return;
        }

        using Stream lvfStream = OpenLvfReadStream(lvfPath);
        using StreamReader reader = new(lvfStream, Encoding.UTF8, true);
        LvfHeader header = ReadHeader(reader);

        int frameCount = 0;
        long pathCount = 0;
        long regionCount = 0;
        long correctionCount = 0;
        long pointCount = 0;
        HashSet<int> regionIds = new();

        while (ReadNextFrame(reader, header) is { } frame)
        {
            frameCount++;
            pathCount += frame.Paths.Count;
            regionCount += frame.Regions.Count;
            correctionCount += frame.Regions.Count(region => region.IsCorrection);
            pointCount += frame.Paths.Sum(path => path.Count) + frame.Regions.Sum(region => region.Points.Count);

            foreach (RegionShape region in frame.Regions)
            {
                if (region.Id > 0)
                {
                    regionIds.Add(region.Id);
                }
            }
        }

        Console.WriteLine($"File: {lvfPath}");
        Console.WriteLine($"Format: {DescribeFormat(header)}");
        Console.WriteLine($"Size: {header.Width}x{header.Height}");
        Console.WriteLine($"FPS: {FormatDouble(header.Fps)}");
        if (header.Quality > 0)
        {
            Console.WriteLine($"Quality: {header.Quality}");
            Console.WriteLine($"Palette: {header.PaletteSize}");
        }

        Console.WriteLine($"Frames: {frameCount}");
        Console.WriteLine($"Regions: {regionCount}");
        Console.WriteLine($"Correction regions: {correctionCount}");
        Console.WriteLine($"Unique region IDs: {regionIds.Count}");
        Console.WriteLine($"Paths: {pathCount}");
        Console.WriteLine($"Points: {pointCount}");
    }

    private static void InspectBinaryLVF(string lvfPath)
    {
        using Stream lvfStream = OpenLvfReadStream(lvfPath);
        using BinaryReader reader = new(lvfStream, Encoding.UTF8);
        LvfHeader header = ReadBinaryHeader(reader);

        int frameCount = 0;
        long regionCount = 0;
        long correctionCount = 0;
        long pointCount = 0;
        HashSet<int> regionIds = new();

        while (ReadNextBinaryRegionFrame(reader, header.BinaryVersion) is { } frame)
        {
            frameCount++;
            regionCount += frame.Regions.Count;
            correctionCount += frame.Regions.Count(region => region.IsCorrection);
            pointCount += frame.Regions.Sum(region => region.Points.Count);

            foreach (RegionShape region in frame.Regions)
            {
                if (region.Id > 0)
                {
                    regionIds.Add(region.Id);
                }
            }
        }

        Console.WriteLine($"File: {lvfPath}");
        Console.WriteLine($"Format: {DescribeFormat(header)}");
        Console.WriteLine("Storage: compressed binary");
        Console.WriteLine($"Size: {header.Width}x{header.Height}");
        Console.WriteLine($"FPS: {FormatDouble(header.Fps)}");
        Console.WriteLine($"Quality: {header.Quality}");
        Console.WriteLine($"Palette: {header.PaletteSize}");
        Console.WriteLine($"Frames: {frameCount}");
        Console.WriteLine($"Regions: {regionCount}");
        Console.WriteLine($"Correction regions: {correctionCount}");
        Console.WriteLine($"Unique region IDs: {regionIds.Count}");
        Console.WriteLine("Paths: 0");
        Console.WriteLine($"Points: {pointCount}");
    }

    private static void ProcessVideoRegions(string videoPath, string outputPath, int quality, int paletteSize, int darkFilter, int patchDetail, int objectFocus, int correctionStrength, AccelerationOptions acceleration, CompressionLevel compressionLevel, RegionTracerMode tracerMode, int pipelineDepth, bool profile)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file was not found.", videoPath);
        }

        quality = Math.Clamp(quality, 1, 100);
        paletteSize = Math.Clamp(paletteSize, 2, 128);
        darkFilter = Math.Clamp(darkFilter, 0, 100);
        patchDetail = Math.Clamp(patchDetail, 0, 100);
        objectFocus = Math.Clamp(objectFocus, 0, 100);
        correctionStrength = Math.Clamp(correctionStrength, 0, 100);
        pipelineDepth = Math.Clamp(pipelineDepth, 1, Math.Max(1, Environment.ProcessorCount / 2));

        VideoInfo info = ProbeVideo(videoPath);
        int channels = 3;
        int frameSize = checked(info.Width * info.Height * channels);

        Console.WriteLine($"Encoding {videoPath}");
        Console.WriteLine($"Input: {info.Width}x{info.Height} @ {FormatDouble(info.Fps)} fps");
        Console.WriteLine($"Regions: quality {quality}, palette {paletteSize}, min area {FormatDouble(MinRegionArea(info.Width, info.Height, quality))} px");
        Console.WriteLine($"Dark filter: {darkFilter}");
        Console.WriteLine($"Patch detail: {patchDetail}");
        Console.WriteLine($"Object focus: {objectFocus}");
        Console.WriteLine($"Corrections: {correctionStrength}");
        Console.WriteLine($"Tracer: {DescribeTracer(tracerMode)}");
        Console.WriteLine($"Pipeline: {pipelineDepth} frame{(pipelineDepth == 1 ? "" : "s")} in flight{(pipelineDepth > 1 ? " (palette reuse disabled between in-flight frames)" : "")}");
        Console.WriteLine($"Acceleration: {DescribeAcceleration(acceleration)}");
        Console.WriteLine($"Compression: {DescribeCompression(compressionLevel)}");

        ConfigureAcceleration(acceleration);

        using Process ffmpeg = StartFfmpegRawVideo(videoPath, "bgr24", acceleration);
        byte[] buffer = new byte[frameSize];

        using RegionFrameWriter writer = CreateRegionFrameWriter(outputPath, compressionLevel);
        writer.WriteHeader(info, quality, paletteSize);
        EncodeProfiler? profiler = profile ? new EncodeProfiler() : null;

        int frameCount = 0;
        int nextRegionId = 1;
        long totalRegions = 0;
        long totalCorrections = 0;
        long totalPoints = 0;
        List<RegionSignature> previousRegions = new();
        Palette? previousPalette = null;
        byte[]? previousFrameBytes = null;
        AccelerationOptions frameAcceleration = pipelineDepth <= 1
            ? acceleration
            : acceleration with { WorkerCount = Math.Max(1, acceleration.WorkerCount / pipelineDepth) };

        if (pipelineDepth <= 1)
        {
            while (true)
            {
                long readStart = Stopwatch.GetTimestamp();
                bool hasFrame = ReadFullFrame(ffmpeg.StandardOutput.BaseStream, buffer);
                profiler?.Add(EncodeStage.DecodeRead, readStart);
                if (!hasFrame)
                {
                    break;
                }

                Palette? paletteSeed = frameCount % 5 == 0 ? null : previousPalette;
                EncodedFrame encodedFrame = TraceRawFrame(frameCount, buffer, previousFrameBytes, info, channels, quality, paletteSize, darkFilter, patchDetail, objectFocus, correctionStrength, frameAcceleration, tracerMode, paletteSeed, profiler);
                previousPalette = encodedFrame.Palette;
                previousFrameBytes = buffer.ToArray();

                WriteFrameResult written = WriteEncodedRegionFrame(encodedFrame, info, writer, previousRegions, ref nextRegionId, profiler);
                previousRegions = written.PreviousRegions;
                totalPoints += written.Points;
                totalRegions += written.Regions;
                totalCorrections += written.Corrections;
                frameCount++;

                if (frameCount % 10 == 0)
                {
                    Console.WriteLine($"Encoded {frameCount} frames | {totalRegions} regions ({totalCorrections} corrections) | {totalPoints} points | {nextRegionId - 1} tracked IDs");
                }
            }
        }
        else
        {
            SortedDictionary<int, Task<EncodedFrame>> inFlight = new();
            int nextFrameToRead = 0;
            int nextFrameToWrite = 0;
            bool doneReading = false;

            while (!doneReading || inFlight.Count > 0)
            {
                while (!doneReading && inFlight.Count < pipelineDepth)
                {
                    long readStart = Stopwatch.GetTimestamp();
                    bool hasFrame = ReadFullFrame(ffmpeg.StandardOutput.BaseStream, buffer);
                    profiler?.Add(EncodeStage.DecodeRead, readStart);
                    if (!hasFrame)
                    {
                        doneReading = true;
                        break;
                    }

                    int frameNumber = nextFrameToRead++;
                    byte[] frameBytes = buffer.ToArray();
                    byte[]? motionReference = previousFrameBytes;
                    previousFrameBytes = frameBytes;
                    inFlight[frameNumber] = Task.Run(() => TraceRawFrame(frameNumber, frameBytes, motionReference, info, channels, quality, paletteSize, darkFilter, patchDetail, objectFocus, correctionStrength, frameAcceleration, tracerMode, null, profiler));
                }

                if (!inFlight.TryGetValue(nextFrameToWrite, out Task<EncodedFrame>? frameTask))
                {
                    if (doneReading)
                    {
                        break;
                    }

                    continue;
                }

                EncodedFrame encodedFrame = frameTask.GetAwaiter().GetResult();
                inFlight.Remove(nextFrameToWrite);

                WriteFrameResult written = WriteEncodedRegionFrame(encodedFrame, info, writer, previousRegions, ref nextRegionId, profiler);
                previousRegions = written.PreviousRegions;
                totalPoints += written.Points;
                totalRegions += written.Regions;
                totalCorrections += written.Corrections;
                nextFrameToWrite++;
                frameCount = nextFrameToWrite;

                if (frameCount % 10 == 0)
                {
                    Console.WriteLine($"Encoded {frameCount} frames | {totalRegions} regions ({totalCorrections} corrections) | {totalPoints} points | {nextRegionId - 1} tracked IDs");
                }
            }
        }

        ffmpeg.WaitForExit();
        if (ffmpeg.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}.");
        }

        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine($"Frames: {frameCount}, regions: {totalRegions}, corrections: {totalCorrections}, points: {totalPoints}, tracked IDs: {nextRegionId - 1}");
        profiler?.Print(frameCount);
    }

    private static EncodedFrame TraceRawFrame(int frameNumber, byte[] frameBytes, byte[]? previousFrameBytes, VideoInfo info, int channels, int quality, int paletteSize, int darkFilter, int patchDetail, int objectFocus, int correctionStrength, AccelerationOptions acceleration, RegionTracerMode tracerMode, Palette? paletteSeed, EncodeProfiler? profiler)
    {
        long copyStart = Stopwatch.GetTimestamp();
        using Mat frame = new(info.Height, info.Width, DepthType.Cv8U, channels);
        Marshal.Copy(frameBytes, 0, frame.DataPointer, frameBytes.Length);
        profiler?.Add(EncodeStage.FrameCopy, copyStart);

        long prepareStart = Stopwatch.GetTimestamp();
        using Mat prepared = PrepareForSegmentation(frame, quality, acceleration);
        profiler?.Add(EncodeStage.Preprocess, prepareStart);

        TracedRegions traced = TraceRegions(prepared, frameBytes, previousFrameBytes, quality, paletteSize, darkFilter, patchDetail, objectFocus, correctionStrength, acceleration, tracerMode, paletteSeed, profiler, out Palette framePalette);
        return new EncodedFrame(frameNumber, traced, framePalette);
    }

    private static WriteFrameResult WriteEncodedRegionFrame(EncodedFrame encodedFrame, VideoInfo info, RegionFrameWriter writer, List<RegionSignature> previousRegions, ref int nextRegionId, EncodeProfiler? profiler)
    {
        List<RegionShape> regions = encodedFrame.Traced.Regions;
        List<RegionShape> trackedRegions = regions.Where(region => !region.IsCorrection).ToList();

        long idStart = Stopwatch.GetTimestamp();
        AssignRegionIds(trackedRegions, previousRegions, info.Width, info.Height, ref nextRegionId);
        List<RegionSignature> nextPreviousRegions = trackedRegions.Select(RegionSignature.FromShape).ToList();
        profiler?.Add(EncodeStage.TrackIds, idStart);

        long writeStart = Stopwatch.GetTimestamp();
        writer.WriteFrame(encodedFrame.FrameNumber, encodedFrame.Traced, regions);
        profiler?.Add(EncodeStage.Write, writeStart);

        return new WriteFrameResult(
            regions.Count,
            regions.Count(region => region.IsCorrection),
            regions.Sum(region => region.Points.Count),
            nextPreviousRegions);
    }

    private static Mat PrepareForSegmentation(Mat frame, int quality, AccelerationOptions acceleration)
    {
        int diameter = quality >= 80 ? 5 : quality >= 60 ? 7 : 9;
        double sigma = quality >= 90 ? 22 : quality >= 80 ? 30 : quality >= 65 ? 40 : 56;

        if (acceleration.UseOpenCl)
        {
            try
            {
                Mat prepared = new();
                using UMat source = frame.GetUMat(AccessType.Read, UMat.Usage.AllocateDeviceMemory);
                using UMat filtered = new(UMat.Usage.AllocateDeviceMemory);
                CvInvoke.BilateralFilter(source, filtered, diameter, sigma, sigma);
                CvInvoke.OclFinish();
                filtered.CopyTo(prepared);
                return prepared;
            }
            catch
            {
                CvInvoke.UseOpenCL = false;
            }
        }

        Mat cpuPrepared = new();
        CvInvoke.BilateralFilter(frame, cpuPrepared, diameter, sigma, sigma);
        return cpuPrepared;
    }

    private static TracedRegions TraceRegions(Mat frame, byte[] sourcePixels, byte[]? previousSourcePixels, int quality, int paletteSize, int darkFilter, int patchDetail, int objectFocus, int correctionStrength, AccelerationOptions acceleration, RegionTracerMode tracerMode, Palette? paletteSeed, EncodeProfiler? profiler, out Palette framePalette)
    {
        int width = frame.Width;
        int height = frame.Height;
        int pixelCount = checked(width * height);
        byte[] pixels;
        if (tracerMode == RegionTracerMode.MergedFast)
        {
            long mergeStart = Stopwatch.GetTimestamp();
            pixels = BuildFastMergedPixels(frame, quality);
            profiler?.Add(EncodeStage.MergePrepass, mergeStart);
        }
        else
        {
            pixels = new byte[pixelCount * 3];
            Marshal.Copy(frame.DataPointer, pixels, 0, pixels.Length);
        }

        byte[] detailPixels = sourcePixels.Length == pixels.Length ? sourcePixels : pixels;
        byte[]? previousDetailPixels = previousSourcePixels is not null && previousSourcePixels.Length == detailPixels.Length
            ? previousSourcePixels
            : null;

        long paletteStart = Stopwatch.GetTimestamp();
        Palette palette = BuildPalette(pixels, width, height, paletteSize, paletteSeed);
        framePalette = palette;
        profiler?.Add(EncodeStage.BuildPalette, paletteStart);

        long labelStart = Stopwatch.GetTimestamp();
        byte[] labels = AssignPaletteLabels(pixels, palette, acceleration, out Color[] colors, out int[] counts);
        profiler?.Add(EncodeStage.AssignLabels, labelStart);

        Color background = SelectBackgroundColor(colors, counts);
        double minArea = MinRegionArea(width, height, quality);
        double simplify = SimplifyForQuality(quality);

        long contourStart = Stopwatch.GetTimestamp();
        List<RegionShape> regions;
        if (tracerMode == RegionTracerMode.OpenCv || tracerMode == RegionTracerMode.MergedFast)
        {
            long maskStart = Stopwatch.GetTimestamp();
            byte[][] masks = BuildLabelMasks(labels, counts, pixelCount, acceleration.WorkerCount);
            profiler?.Add(EncodeStage.BuildMasks, maskStart);
            regions = TraceLabelRegionsWithOpenCv(masks, counts, colors, width, height, minArea, simplify, acceleration.WorkerCount);
        }
        else if (tracerMode == RegionTracerMode.Merged)
        {
            regions = TraceMergedNeighborRegions(detailPixels, width, height, quality, paletteSize, MergedRegionArea(width, height, quality), simplify);
        }
        else
        {
            regions = TraceLabelRegionsCustom(labels, colors, width, height, minArea, simplify);
        }

        profiler?.Add(EncodeStage.TraceContours, contourStart);

        List<RegionShape> orderedRegions = SuppressDarkSpeckles(regions, width, height, quality, darkFilter)
            .OrderByDescending(region => region.Area)
            .ToList();

        long objectStart = Stopwatch.GetTimestamp();
        byte[]? foregroundMask = BuildForegroundMask(detailPixels, previousDetailPixels, width, height, objectFocus, acceleration.WorkerCount);
        profiler?.Add(EncodeStage.ObjectMask, objectStart);

        long detailStart = Stopwatch.GetTimestamp();
        List<RegionShape> detailRegions = TracePatchDetailRegions(detailPixels, foregroundMask, objectFocus, background, orderedRegions, width, height, quality, patchDetail, acceleration.WorkerCount);
        profiler?.Add(EncodeStage.TraceDetails, detailStart);
        orderedRegions.AddRange(detailRegions);

        long residualStart = Stopwatch.GetTimestamp();
        if (correctionStrength > 0)
        {
            List<RegionShape> corrections = TraceErrorRegions(frame, detailPixels, background, orderedRegions, quality, darkFilter, correctionStrength, acceleration.WorkerCount);
            orderedRegions.AddRange(corrections);
        }

        profiler?.Add(EncodeStage.TraceResiduals, residualStart);

        return new TracedRegions(background, orderedRegions);
    }

    private static List<RegionShape> TraceLabelRegionsWithOpenCv(byte[][] masks, int[] counts, Color[] colors, int width, int height, double minArea, double simplify, int workerCount)
    {
        List<RegionShape> regions = new();
        object regionsLock = new();
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, workerCount) };

        Parallel.For(
            0,
            colors.Length,
            parallelOptions,
            () => new List<RegionShape>(),
            (label, _, localRegions) =>
        {
            if (counts[label] == 0)
            {
                return localRegions;
            }

            byte[] maskBytes = masks[label];

            using Mat mask = new(height, width, DepthType.Cv8U, 1);
            Marshal.Copy(maskBytes, 0, mask.DataPointer, maskBytes.Length);

            using VectorOfVectorOfPoint contours = new();
            using Mat hierarchy = new();
            CvInvoke.FindContours(mask, contours, hierarchy, RetrType.External, ChainApproxMethod.ChainApproxSimple);

            for (int i = 0; i < contours.Size; i++)
            {
                using VectorOfPoint contour = contours[i];
                double area = Math.Abs(CvInvoke.ContourArea(contour));
                if (area < minArea)
                {
                    continue;
                }

                using VectorOfPoint simplified = new();
                CvInvoke.ApproxPolyDP(contour, simplified, simplify, true);
                Point[] points = simplified.ToArray();
                if (points.Length < 3)
                {
                    continue;
                }

                Rectangle bounds = CvInvoke.BoundingRectangle(simplified);
                localRegions.Add(new RegionShape
                {
                    Fill = colors[label],
                    Points = points.ToList(),
                    Area = area,
                    Center = AveragePoint(points),
                    Bounds = bounds
                });
            }

            return localRegions;
        },
            localRegions =>
            {
                if (localRegions.Count == 0)
                {
                    return;
                }

                lock (regionsLock)
                {
                    regions.AddRange(localRegions);
                }
            });

        return regions;
    }

    private static List<RegionShape> TraceLabelRegionsCustom(byte[] labels, Color[] colors, int width, int height, double minArea, double simplify)
    {
        int pixelCount = checked(width * height);
        byte[] visited = new byte[pixelCount];
        int[] queue = new int[pixelCount];
        List<RegionShape> regions = new();

        for (int startPixel = 0; startPixel < pixelCount; startPixel++)
        {
            if (visited[startPixel] != 0)
            {
                continue;
            }

            byte label = labels[startPixel];
            int head = 0;
            int tail = 0;
            queue[tail++] = startPixel;
            visited[startPixel] = 1;

            int area = 0;
            int minX = width;
            int minY = height;
            int maxX = 0;
            int maxY = 0;
            long sumX = 0;
            long sumY = 0;

            while (head < tail)
            {
                int pixel = queue[head++];
                int x = pixel % width;
                int y = pixel / width;

                area++;
                sumX += x;
                sumY += y;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                VisitSameLabelNeighbor(labels, visited, queue, ref tail, label, width, height, x, y - 1);
                VisitSameLabelNeighbor(labels, visited, queue, ref tail, label, width, height, x + 1, y);
                VisitSameLabelNeighbor(labels, visited, queue, ref tail, label, width, height, x, y + 1);
                VisitSameLabelNeighbor(labels, visited, queue, ref tail, label, width, height, x - 1, y);
            }

            if (area < minArea)
            {
                continue;
            }

            List<Edge> edges = BuildBoundaryEdges(labels, queue, tail, label, width, height);
            if (edges.Count < 3)
            {
                continue;
            }

            List<Point> outline = TraceLargestBoundaryLoop(edges, width + 1);
            outline = SimplifyClosedPolygon(RemoveCollinearPoints(outline), simplify);
            if (outline.Count < 3)
            {
                continue;
            }

            regions.Add(new RegionShape
            {
                Fill = colors[label],
                Points = outline,
                Area = area,
                Center = new PointF((float)(sumX / (double)area), (float)(sumY / (double)area)),
                Bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1)
            });
        }

        return regions;
    }

    private static List<RegionShape> TraceMergedNeighborRegions(byte[] sourcePixels, int width, int height, int quality, int paletteSize, double minArea, double simplify)
    {
        int pixelCount = checked(width * height);
        byte[] guidePixels = BuildTinyBlurPixels(sourcePixels, width, height);
        DisjointSet sets = new(pixelCount);

        int initialThresholdSquared = Square(InitialNeighborMergeThreshold(quality));
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x;
                int offset = pixel * 3;
                if (x > 0 && PixelDistanceSquared(sourcePixels, offset, offset - 3) <= initialThresholdSquared)
                {
                    sets.Union(pixel, pixel - 1);
                }

                if (y > 0 && PixelDistanceSquared(sourcePixels, offset, offset - width * 3) <= initialThresholdSquared)
                {
                    sets.Union(pixel, pixel - width);
                }
            }
        }

        int[] counts = new int[pixelCount];
        long[] sumB = new long[pixelCount];
        long[] sumG = new long[pixelCount];
        long[] sumR = new long[pixelCount];
        BuildMergeColorStats(sourcePixels, sets, counts, sumB, sumG, sumR);

        int guideThresholdSquared = Square(BlurredNeighborBoundaryThreshold(quality));
        int guideRegionThresholdSquared = Square(BlurredNeighborRegionThreshold(quality));
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x;
                int offset = pixel * 3;
                if (x > 0)
                {
                    TryMeanGuidedUnion(sets, counts, sumB, sumG, sumR, guidePixels, pixel, pixel - 1, offset, offset - 3, guideThresholdSquared, guideRegionThresholdSquared);
                }

                if (y > 0)
                {
                    TryMeanGuidedUnion(sets, counts, sumB, sumG, sumR, guidePixels, pixel, pixel - width, offset, offset - width * 3, guideThresholdSquared, guideRegionThresholdSquared);
                }
            }
        }

        Palette mergePalette = BuildRegionMergePalette(counts, sumB, sumG, sumR, paletteSize);
        int[] nearestPalette = Enumerable.Repeat(-1, pixelCount).ToArray();
        double[] paletteDistanceSquared = new double[pixelCount];
        AssignRegionPaletteFits(mergePalette, counts, sumB, sumG, sumR, nearestPalette, paletteDistanceSquared);

        int paletteRangeSquared = Square(PaletteFitMergeThreshold(quality, paletteSize));
        int regionRangeSquared = Square(PaletteGuidedRegionMergeThreshold(quality, paletteSize));
        int largeRegionRangeSquared = Square(PaletteGuidedLargeRegionMergeThreshold(quality));
        int boundaryRangeSquared = Square(PaletteGuidedBoundaryMergeThreshold(quality, paletteSize));
        int absorbLimit = PaletteGuidedAbsorbLimit(width, height, quality);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x;
                int offset = pixel * 3;
                if (x > 0)
                {
                    TryPaletteGuidedUnion(sets, mergePalette, counts, sumB, sumG, sumR, nearestPalette, paletteDistanceSquared, sourcePixels, guidePixels, pixel, pixel - 1, offset, offset - 3, paletteRangeSquared, regionRangeSquared, largeRegionRangeSquared, boundaryRangeSquared, absorbLimit);
                }

                if (y > 0)
                {
                    TryPaletteGuidedUnion(sets, mergePalette, counts, sumB, sumG, sumR, nearestPalette, paletteDistanceSquared, sourcePixels, guidePixels, pixel, pixel - width, offset, offset - width * 3, paletteRangeSquared, regionRangeSquared, largeRegionRangeSquared, boundaryRangeSquared, absorbLimit);
                }
            }
        }

        int[] labels = new int[pixelCount];
        int[] finalCounts = new int[pixelCount];
        long[] finalSumB = new long[pixelCount];
        long[] finalSumG = new long[pixelCount];
        long[] finalSumR = new long[pixelCount];
        int[] minX = Enumerable.Repeat(width, pixelCount).ToArray();
        int[] minY = Enumerable.Repeat(height, pixelCount).ToArray();
        int[] maxX = Enumerable.Repeat(-1, pixelCount).ToArray();
        int[] maxY = Enumerable.Repeat(-1, pixelCount).ToArray();

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int root = sets.Find(pixel);
            labels[pixel] = root;
            int offset = pixel * 3;
            int x = pixel % width;
            int y = pixel / width;

            finalCounts[root]++;
            finalSumB[root] += sourcePixels[offset];
            finalSumG[root] += sourcePixels[offset + 1];
            finalSumR[root] += sourcePixels[offset + 2];
            minX[root] = Math.Min(minX[root], x);
            minY[root] = Math.Min(minY[root], y);
            maxX[root] = Math.Max(maxX[root], x);
            maxY[root] = Math.Max(maxY[root], y);
        }

        HashSet<int> activeRoots = new();
        for (int root = 0; root < pixelCount; root++)
        {
            if (finalCounts[root] >= minArea)
            {
                activeRoots.Add(root);
            }
        }

        if (activeRoots.Count == 0)
        {
            return new List<RegionShape>();
        }

        Dictionary<int, List<int>> pixelsByRoot = new(activeRoots.Count);
        foreach (int root in activeRoots)
        {
            pixelsByRoot[root] = new List<int>(Math.Min(finalCounts[root], 8192));
        }

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int root = labels[pixel];
            if (pixelsByRoot.TryGetValue(root, out List<int>? regionPixels))
            {
                regionPixels.Add(pixel);
            }
        }

        List<RegionShape> regions = new(pixelsByRoot.Count);
        foreach ((int root, List<int> regionPixels) in pixelsByRoot)
        {
            List<Edge> edges = BuildBoundaryEdges(labels, regionPixels, root, width, height);
            if (edges.Count < 3)
            {
                continue;
            }

            List<Point> outline = TraceLargestBoundaryLoop(edges, width + 1);
            outline = SimplifyClosedPolygon(RemoveCollinearPoints(outline), simplify);
            if (outline.Count < 3)
            {
                continue;
            }

            int count = Math.Max(1, finalCounts[root]);
            Color fill = Color.FromArgb(
                ClampByte((int)Math.Round(finalSumR[root] / (double)count)),
                ClampByte((int)Math.Round(finalSumG[root] / (double)count)),
                ClampByte((int)Math.Round(finalSumB[root] / (double)count)));

            regions.Add(new RegionShape
            {
                Fill = fill,
                Points = outline,
                Area = finalCounts[root],
                Center = new PointF((minX[root] + maxX[root]) / 2f, (minY[root] + maxY[root]) / 2f),
                Bounds = Rectangle.FromLTRB(minX[root], minY[root], maxX[root] + 1, maxY[root] + 1)
            });
        }

        return regions;
    }

    private static byte[] BuildTinyBlurPixels(byte[] sourcePixels, int width, int height)
    {
        using Mat source = new(height, width, DepthType.Cv8U, 3);
        Marshal.Copy(sourcePixels, 0, source.DataPointer, sourcePixels.Length);
        using Mat blurred = new();
        CvInvoke.GaussianBlur(source, blurred, new Size(3, 3), 0.65);

        byte[] pixels = new byte[sourcePixels.Length];
        Marshal.Copy(blurred.DataPointer, pixels, 0, pixels.Length);
        return pixels;
    }

    private static byte[] BuildFastMergedPixels(Mat frame, int quality)
    {
        using Mat merged = new();
        CvInvoke.GaussianBlur(frame, merged, new Size(3, 3), 0.65);

        byte[] pixels = new byte[frame.Width * frame.Height * 3];
        Marshal.Copy(merged.DataPointer, pixels, 0, pixels.Length);
        SnapPixelsToLocalColorBands(pixels, FastMergedColorStep(quality));
        return pixels;
    }

    private static void SnapPixelsToLocalColorBands(byte[] pixels, int step)
    {
        if (step <= 1)
        {
            return;
        }

        int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, pixels.Length / 262144 + 1));
        Parallel.For(0, workers, worker =>
        {
            int start = pixels.Length * worker / workers;
            int end = pixels.Length * (worker + 1) / workers;
            for (int i = start; i < end; i++)
            {
                int snapped = (pixels[i] + step / 2) / step * step;
                pixels[i] = (byte)ClampByte(snapped);
            }
        });
    }

    private static void BuildMergeColorStats(byte[] sourcePixels, DisjointSet sets, int[] counts, long[] sumB, long[] sumG, long[] sumR)
    {
        int pixelCount = sourcePixels.Length / 3;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int root = sets.Find(pixel);
            int offset = pixel * 3;
            counts[root]++;
            sumB[root] += sourcePixels[offset];
            sumG[root] += sourcePixels[offset + 1];
            sumR[root] += sourcePixels[offset + 2];
        }
    }

    private static Palette BuildRegionMergePalette(int[] counts, long[] sumB, long[] sumG, long[] sumR, int requestedPaletteSize)
    {
        List<MergeColorSample> samples = new();
        for (int root = 0; root < counts.Length; root++)
        {
            int count = counts[root];
            if (count <= 0)
            {
                continue;
            }

            double b = sumB[root] / (double)count;
            double g = sumG[root] / (double)count;
            double r = sumR[root] / (double)count;
            double weight = Math.Clamp(Math.Sqrt(count), 1, 96);
            samples.Add(new MergeColorSample(b, g, r, weight));
        }

        if (samples.Count == 0)
        {
            return new Palette(new[] { 0.0 }, new[] { 0.0 }, new[] { 0.0 });
        }

        samples.Sort((a, b) => MergeSampleLuminance(a).CompareTo(MergeSampleLuminance(b)));
        int paletteSize = Math.Clamp(requestedPaletteSize, 1, samples.Count);
        double[] centerB = new double[paletteSize];
        double[] centerG = new double[paletteSize];
        double[] centerR = new double[paletteSize];

        for (int i = 0; i < paletteSize; i++)
        {
            MergeColorSample sample = samples[(int)Math.Round(i * (samples.Count - 1) / Math.Max(1.0, paletteSize - 1.0))];
            centerB[i] = sample.B;
            centerG[i] = sample.G;
            centerR[i] = sample.R;
        }

        double[] weightSums = new double[paletteSize];
        double[] sumCenterB = new double[paletteSize];
        double[] sumCenterG = new double[paletteSize];
        double[] sumCenterR = new double[paletteSize];

        for (int iteration = 0; iteration < 6; iteration++)
        {
            Array.Clear(weightSums);
            Array.Clear(sumCenterB);
            Array.Clear(sumCenterG);
            Array.Clear(sumCenterR);

            foreach (MergeColorSample sample in samples)
            {
                int nearest = FindNearestPaletteColor(sample.B, sample.G, sample.R, centerB, centerG, centerR);
                weightSums[nearest] += sample.Weight;
                sumCenterB[nearest] += sample.B * sample.Weight;
                sumCenterG[nearest] += sample.G * sample.Weight;
                sumCenterR[nearest] += sample.R * sample.Weight;
            }

            for (int i = 0; i < paletteSize; i++)
            {
                if (weightSums[i] <= 0)
                {
                    continue;
                }

                centerB[i] = sumCenterB[i] / weightSums[i];
                centerG[i] = sumCenterG[i] / weightSums[i];
                centerR[i] = sumCenterR[i] / weightSums[i];
            }
        }

        return new Palette(centerB, centerG, centerR);
    }

    private static double MergeSampleLuminance(MergeColorSample sample)
    {
        return sample.R * 0.2126 + sample.G * 0.7152 + sample.B * 0.0722;
    }

    private static void AssignRegionPaletteFits(Palette palette, int[] counts, long[] sumB, long[] sumG, long[] sumR, int[] nearestPalette, double[] paletteDistanceSquared)
    {
        for (int root = 0; root < counts.Length; root++)
        {
            int count = counts[root];
            if (count == 0)
            {
                continue;
            }

            double b = sumB[root] / (double)count;
            double g = sumG[root] / (double)count;
            double r = sumR[root] / (double)count;
            int nearest = FindNearestPaletteColor(
                (byte)ClampByte((int)Math.Round(b)),
                (byte)ClampByte((int)Math.Round(g)),
                (byte)ClampByte((int)Math.Round(r)),
                palette.B,
                palette.G,
                palette.R);
            nearestPalette[root] = nearest;

            double db = b - palette.B[nearest];
            double dg = g - palette.G[nearest];
            double dr = r - palette.R[nearest];
            paletteDistanceSquared[root] = db * db + dg * dg + dr * dr;
        }
    }

    private static void TryMeanGuidedUnion(DisjointSet sets, int[] counts, long[] sumB, long[] sumG, long[] sumR, byte[] guidePixels, int pixelA, int pixelB, int offsetA, int offsetB, int boundaryRangeSquared, int regionRangeSquared)
    {
        int rootA = sets.Find(pixelA);
        int rootB = sets.Find(pixelB);
        if (rootA == rootB)
        {
            return;
        }

        if (PixelDistanceSquared(guidePixels, offsetA, offsetB) > boundaryRangeSquared)
        {
            return;
        }

        if (RegionMeanDistanceSquared(counts, sumB, sumG, sumR, rootA, rootB) > regionRangeSquared)
        {
            return;
        }

        if (sets.Union(rootA, rootB, out int mergedRoot, out int absorbedRoot))
        {
            MergeStats(counts, sumB, sumG, sumR, mergedRoot, absorbedRoot);
        }
    }

    private static void TryPaletteGuidedUnion(DisjointSet sets, Palette palette, int[] counts, long[] sumB, long[] sumG, long[] sumR, int[] nearestPalette, double[] paletteDistanceSquared, byte[] sourcePixels, byte[] guidePixels, int pixelA, int pixelB, int offsetA, int offsetB, int paletteRangeSquared, int regionRangeSquared, int largeRegionRangeSquared, int boundaryRangeSquared, int absorbLimit)
    {
        int rootA = sets.Find(pixelA);
        int rootB = sets.Find(pixelB);
        if (rootA == rootB)
        {
            return;
        }

        if (nearestPalette[rootA] < 0 || nearestPalette[rootA] != nearestPalette[rootB])
        {
            return;
        }

        if (paletteDistanceSquared[rootA] > paletteRangeSquared || paletteDistanceSquared[rootB] > paletteRangeSquared)
        {
            return;
        }

        double regionDistanceSquared = RegionMeanDistanceSquared(counts, sumB, sumG, sumR, rootA, rootB);
        if (regionDistanceSquared > regionRangeSquared)
        {
            return;
        }

        int smallerRegion = Math.Min(counts[rootA], counts[rootB]);
        if (smallerRegion > absorbLimit && regionDistanceSquared > largeRegionRangeSquared)
        {
            return;
        }

        if (PixelDistanceSquared(guidePixels, offsetA, offsetB) > boundaryRangeSquared ||
            PixelDistanceSquared(sourcePixels, offsetA, offsetB) > boundaryRangeSquared * 2)
        {
            return;
        }

        if (sets.Union(rootA, rootB, out int mergedRoot, out int absorbedRoot))
        {
            int paletteIndex = nearestPalette[rootA];
            MergeStats(counts, sumB, sumG, sumR, mergedRoot, absorbedRoot);
            nearestPalette[mergedRoot] = paletteIndex;
            paletteDistanceSquared[mergedRoot] = RegionPaletteDistanceSquared(palette, counts, sumB, sumG, sumR, mergedRoot, paletteIndex);
            nearestPalette[absorbedRoot] = -1;
            paletteDistanceSquared[absorbedRoot] = 0;
        }
    }

    private static double RegionMeanDistanceSquared(int[] counts, long[] sumB, long[] sumG, long[] sumR, int rootA, int rootB)
    {
        int countA = Math.Max(1, counts[rootA]);
        int countB = Math.Max(1, counts[rootB]);
        double db = sumB[rootA] / (double)countA - sumB[rootB] / (double)countB;
        double dg = sumG[rootA] / (double)countA - sumG[rootB] / (double)countB;
        double dr = sumR[rootA] / (double)countA - sumR[rootB] / (double)countB;
        return db * db + dg * dg + dr * dr;
    }

    private static void MergeStats(int[] counts, long[] sumB, long[] sumG, long[] sumR, int mergedRoot, int absorbedRoot)
    {
        if (mergedRoot == absorbedRoot)
        {
            return;
        }

        counts[mergedRoot] += counts[absorbedRoot];
        sumB[mergedRoot] += sumB[absorbedRoot];
        sumG[mergedRoot] += sumG[absorbedRoot];
        sumR[mergedRoot] += sumR[absorbedRoot];
        counts[absorbedRoot] = 0;
        sumB[absorbedRoot] = 0;
        sumG[absorbedRoot] = 0;
        sumR[absorbedRoot] = 0;
    }

    private static double RegionPaletteDistanceSquared(Palette palette, int[] counts, long[] sumB, long[] sumG, long[] sumR, int root, int paletteIndex)
    {
        int count = Math.Max(1, counts[root]);
        double b = sumB[root] / (double)count;
        double g = sumG[root] / (double)count;
        double r = sumR[root] / (double)count;
        double db = b - palette.B[paletteIndex];
        double dg = g - palette.G[paletteIndex];
        double dr = r - palette.R[paletteIndex];
        return db * db + dg * dg + dr * dr;
    }

    private static List<Edge> BuildBoundaryEdges(int[] labels, IReadOnlyList<int> pixels, int label, int width, int height)
    {
        List<Edge> edges = new(Math.Max(16, pixels.Count / 4));
        foreach (int pixel in pixels)
        {
            int x = pixel % width;
            int y = pixel / width;

            if (y == 0 || labels[pixel - width] != label)
            {
                edges.Add(new Edge(x, y, x + 1, y));
            }

            if (x == width - 1 || labels[pixel + 1] != label)
            {
                edges.Add(new Edge(x + 1, y, x + 1, y + 1));
            }

            if (y == height - 1 || labels[pixel + width] != label)
            {
                edges.Add(new Edge(x + 1, y + 1, x, y + 1));
            }

            if (x == 0 || labels[pixel - 1] != label)
            {
                edges.Add(new Edge(x, y + 1, x, y));
            }
        }

        return edges;
    }

    private static void VisitSameLabelNeighbor(byte[] labels, byte[] visited, int[] queue, ref int tail, byte label, int width, int height, int nx, int ny)
    {
        if (nx < 0 || nx >= width || ny < 0 || ny >= height)
        {
            return;
        }

        int neighbor = ny * width + nx;
        if (labels[neighbor] != label)
        {
            return;
        }

        if (visited[neighbor] == 0)
        {
            visited[neighbor] = 1;
            queue[tail++] = neighbor;
        }
    }

    private static List<Edge> BuildBoundaryEdges(byte[] labels, int[] pixels, int count, byte label, int width, int height)
    {
        List<Edge> edges = new(Math.Max(16, count / 4));
        for (int i = 0; i < count; i++)
        {
            int pixel = pixels[i];
            int x = pixel % width;
            int y = pixel / width;

            if (y == 0 || labels[pixel - width] != label)
            {
                edges.Add(new Edge(x, y, x + 1, y));
            }

            if (x == width - 1 || labels[pixel + 1] != label)
            {
                edges.Add(new Edge(x + 1, y, x + 1, y + 1));
            }

            if (y == height - 1 || labels[pixel + width] != label)
            {
                edges.Add(new Edge(x + 1, y + 1, x, y + 1));
            }

            if (x == 0 || labels[pixel - 1] != label)
            {
                edges.Add(new Edge(x, y + 1, x, y));
            }
        }

        return edges;
    }

    private static List<Point> TraceLargestBoundaryLoop(List<Edge> edges, int vertexStride)
    {
        Dictionary<int, Queue<int>> nextByStart = new(edges.Count);
        foreach (Edge edge in edges)
        {
            int start = VertexKey(edge.X1, edge.Y1, vertexStride);
            int end = VertexKey(edge.X2, edge.Y2, vertexStride);
            if (!nextByStart.TryGetValue(start, out Queue<int>? next))
            {
                next = new Queue<int>();
                nextByStart[start] = next;
            }

            next.Enqueue(end);
        }

        List<Point> largest = new();
        double largestArea = 0;

        foreach (Edge edge in edges)
        {
            int start = VertexKey(edge.X1, edge.Y1, vertexStride);
            if (!nextByStart.TryGetValue(start, out Queue<int>? starts) || starts.Count == 0)
            {
                continue;
            }

            List<Point> loop = new();
            int current = start;
            int guard = edges.Count + 2;

            while (guard-- > 0)
            {
                loop.Add(PointFromVertexKey(current, vertexStride));
                if (!nextByStart.TryGetValue(current, out Queue<int>? next) || next.Count == 0)
                {
                    break;
                }

                current = next.Dequeue();
                if (current == start)
                {
                    break;
                }
            }

            if (loop.Count < 3)
            {
                continue;
            }

            double area = Math.Abs(PolygonArea(loop));
            if (area > largestArea)
            {
                largestArea = area;
                largest = loop;
            }
        }

        return largest;
    }

    private static int VertexKey(int x, int y, int stride)
    {
        return y * stride + x;
    }

    private static Point PointFromVertexKey(int key, int stride)
    {
        return new Point(key % stride, key / stride);
    }

    private static List<Point> RemoveCollinearPoints(List<Point> points)
    {
        if (points.Count <= 3)
        {
            return points;
        }

        List<Point> cleaned = new(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Point previous = points[(i - 1 + points.Count) % points.Count];
            Point current = points[i];
            Point next = points[(i + 1) % points.Count];
            int dx1 = current.X - previous.X;
            int dy1 = current.Y - previous.Y;
            int dx2 = next.X - current.X;
            int dy2 = next.Y - current.Y;
            if (dx1 * dy2 == dy1 * dx2)
            {
                continue;
            }

            cleaned.Add(current);
        }

        return cleaned.Count >= 3 ? cleaned : points;
    }

    private static List<Point> SimplifyClosedPolygon(List<Point> points, double tolerance)
    {
        if (points.Count <= 3 || tolerance <= 0)
        {
            return points;
        }

        (int a, int b) = FindSplitPair(points);
        List<Point> first = SliceClosedPath(points, a, b);
        List<Point> second = SliceClosedPath(points, b, a);
        List<Point> simplified = SimplifyOpenPath(first, tolerance);
        List<Point> other = SimplifyOpenPath(second, tolerance);

        if (other.Count > 1)
        {
            simplified.AddRange(other.Skip(1));
        }

        if (simplified.Count > 1 && simplified[0] == simplified[^1])
        {
            simplified.RemoveAt(simplified.Count - 1);
        }

        return simplified.Count >= 3 ? simplified : points;
    }

    private static (int A, int B) FindSplitPair(List<Point> points)
    {
        int minX = 0;
        int maxX = 0;
        int minY = 0;
        int maxY = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].X < points[minX].X)
            {
                minX = i;
            }

            if (points[i].X > points[maxX].X)
            {
                maxX = i;
            }

            if (points[i].Y < points[minY].Y)
            {
                minY = i;
            }

            if (points[i].Y > points[maxY].Y)
            {
                maxY = i;
            }
        }

        double xDistance = DistanceSquared(points[minX], points[maxX]);
        double yDistance = DistanceSquared(points[minY], points[maxY]);
        return xDistance >= yDistance ? (minX, maxX) : (minY, maxY);
    }

    private static List<Point> SliceClosedPath(List<Point> points, int start, int end)
    {
        List<Point> slice = new();
        int index = start;
        while (true)
        {
            slice.Add(points[index]);
            if (index == end)
            {
                return slice;
            }

            index = (index + 1) % points.Count;
        }
    }

    private static List<Point> SimplifyOpenPath(List<Point> points, double tolerance)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        double toleranceSquared = tolerance * tolerance;
        Stack<(int Start, int End)> stack = new();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            (int start, int end) = stack.Pop();
            if (end <= start + 1)
            {
                continue;
            }

            double bestDistance = 0;
            int bestIndex = -1;
            for (int i = start + 1; i < end; i++)
            {
                double distance = DistanceToSegmentSquared(points[i], points[start], points[end]);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestDistance > toleranceSquared)
            {
                keep[bestIndex] = true;
                stack.Push((start, bestIndex));
                stack.Push((bestIndex, end));
            }
        }

        List<Point> simplified = new();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                simplified.Add(points[i]);
            }
        }

        return simplified;
    }

    private static List<RegionShape> SuppressDarkSpeckles(List<RegionShape> regions, int width, int height, int quality, int darkFilter)
    {
        if (darkFilter <= 0)
        {
            return regions;
        }

        double minArea = MinRegionArea(width, height, quality);
        double frameArea = Math.Max(1, width * height);
        return regions
            .Where(region => !IsLikelyDarkSpeckle(region.Fill, region.Area, region.Bounds, minArea, frameArea, darkFilter))
            .ToList();
    }

    private static byte[]? BuildForegroundMask(byte[] currentPixels, byte[]? previousPixels, int width, int height, int objectFocus, int workerCount)
    {
        if (objectFocus <= 0 || previousPixels is null || previousPixels.Length != currentPixels.Length)
        {
            return null;
        }

        int pixelCount = width * height;
        byte[] mask = new byte[pixelCount];
        int threshold = ForegroundDifferenceThreshold(objectFocus);
        int workers = Math.Max(1, workerCount);
        int[] localCounts = new int[workers];

        Parallel.For(0, workers, worker =>
        {
            int start = pixelCount * worker / workers;
            int end = pixelCount * (worker + 1) / workers;
            int count = 0;
            for (int pixel = start; pixel < end; pixel++)
            {
                int offset = pixel * 3;
                int db = Math.Abs(currentPixels[offset] - previousPixels[offset]);
                int dg = Math.Abs(currentPixels[offset + 1] - previousPixels[offset + 1]);
                int dr = Math.Abs(currentPixels[offset + 2] - previousPixels[offset + 2]);
                int max = Math.Max(db, Math.Max(dg, dr));
                int total = db + dg + dr;
                if (max >= threshold && total >= threshold * 2)
                {
                    mask[pixel] = 255;
                    count++;
                }
            }

            localCounts[worker] = count;
        });

        int active = localCounts.Sum();
        double activeRatio = active / (double)Math.Max(1, pixelCount);
        if (activeRatio < 0.0015 || activeRatio > 0.7)
        {
            return null;
        }

        using Mat rawMask = new(height, width, DepthType.Cv8U, 1);
        Marshal.Copy(mask, 0, rawMask.DataPointer, mask.Length);
        using Mat cleaned = CleanForegroundMask(rawMask, objectFocus);
        byte[] cleanedPixels = new byte[pixelCount];
        Marshal.Copy(cleaned.DataPointer, cleanedPixels, 0, cleanedPixels.Length);
        return cleanedPixels;
    }

    private static Mat CleanForegroundMask(Mat mask, int objectFocus)
    {
        using Mat closed = new();
        Mat cleaned = new();
        int kernelSize = objectFocus >= 70 ? 11 : objectFocus >= 40 ? 7 : 5;
        int dilateIterations = objectFocus >= 70 ? 2 : 1;
        using Mat kernel = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(kernelSize, kernelSize), new Point(-1, -1));

        CvInvoke.MorphologyEx(mask, closed, MorphOp.Close, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.Dilate(closed, cleaned, kernel, new Point(-1, -1), dilateIterations, BorderType.Default, new MCvScalar());
        return cleaned;
    }

    private static List<RegionShape> TracePatchDetailRegions(byte[] sourcePixels, byte[]? foregroundMask, int objectFocus, Color background, List<RegionShape> baseRegions, int width, int height, int quality, int patchDetail, int workerCount)
    {
        if (patchDetail <= 0 || baseRegions.Count == 0)
        {
            return new List<RegionShape>();
        }

        int maxDetailRegions = MaxPatchDetailRegionsForQuality(quality, patchDetail);
        double sourceRegionMinArea = PatchDetailSourceRegionArea(width, height, quality, patchDetail);
        int maxSourceRegions = MaxPatchDetailSourceRegions(patchDetail);

        List<RegionShape> sourceRegions = baseRegions
            .Where(region => !region.IsCorrection &&
                region.Points.Count >= 3 &&
                region.Area >= sourceRegionMinArea &&
                region.Bounds.Width >= PatchDetailMinBounds(quality, patchDetail) &&
                region.Bounds.Height >= PatchDetailMinBounds(quality, patchDetail))
            .OrderByDescending(region => PatchDetailSourceRegionScore(region, foregroundMask, width, height, objectFocus))
            .Take(maxSourceRegions)
            .ToList();

        if (sourceRegions.Count == 0)
        {
            return new List<RegionShape>();
        }

        using Mat prediction = RenderRegionFrame(baseRegions, background, null, width, height);
        byte[] predictedPixels = new byte[sourcePixels.Length];
        Marshal.Copy(prediction.DataPointer, predictedPixels, 0, predictedPixels.Length);

        List<(RegionShape Shape, double Score)> candidates = new();
        object candidatesLock = new();
        int workers = Math.Max(1, Math.Min(workerCount, sourceRegions.Count));

        Parallel.ForEach(
            sourceRegions,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            region =>
        {
            List<(RegionShape Shape, double Score)> localCandidates = BuildPatchDetailCandidatesForRegion(
                sourcePixels,
                predictedPixels,
                foregroundMask,
                objectFocus,
                region,
                width,
                height,
                quality,
                patchDetail);

            if (localCandidates.Count == 0)
            {
                return;
            }

            lock (candidatesLock)
            {
                candidates.AddRange(localCandidates);
            }
        });

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(maxDetailRegions)
            .Select(candidate => candidate.Shape)
            .ToList();
    }

    private static List<(RegionShape Shape, double Score)> BuildPatchDetailCandidatesForRegion(byte[] sourcePixels, byte[] predictedPixels, byte[]? foregroundMask, int objectFocus, RegionShape region, int width, int height, int quality, int patchDetail)
    {
        Rectangle clip = Rectangle.Intersect(region.Bounds, new Rectangle(0, 0, width, height));
        if (clip.Width <= 2 || clip.Height <= 2)
        {
            return new List<(RegionShape Shape, double Score)>();
        }

        byte[] regionMask = BuildRegionMask(region, clip);
        int left = clip.Left;
        int top = clip.Top;
        int rightExclusive = clip.Right;
        int bottomExclusive = clip.Bottom;
        int maskWidth = clip.Width;
        double errorThreshold = PatchDetailErrorThreshold(quality, patchDetail);
        double ownerThreshold = PatchDetailOwnerThreshold(quality, patchDetail);
        const int binCount = 4096;

        int[] binCounts = new int[binCount];
        long[] sumB = new long[binCount];
        long[] sumG = new long[binCount];
        long[] sumR = new long[binCount];
        double[] errorScores = new double[binCount];

        for (int y = top; y < bottomExclusive; y++)
        {
            int row = y * width;
            int maskRow = (y - top) * maskWidth;
            for (int x = left; x < rightExclusive; x++)
            {
                int pixel = row + x;
                int maskIndex = maskRow + x - left;
                if (regionMask[maskIndex] == 0)
                {
                    continue;
                }

                int offset = pixel * 3;
                if (!PredictedBelongsToRegion(predictedPixels, offset, region.Fill, ownerThreshold))
                {
                    continue;
                }

                double error = PixelColorDistance(sourcePixels, predictedPixels, offset);
                bool isForeground = IsForegroundPixel(foregroundMask, pixel);
                double effectiveThreshold = PatchDetailEffectiveThreshold(errorThreshold, objectFocus, isForeground, foregroundMask is not null);
                if (error < effectiveThreshold)
                {
                    continue;
                }

                double weightedError = PatchDetailWeightedError(error, objectFocus, isForeground, foregroundMask is not null);
                int bin = QuantizeDetailColor(sourcePixels, offset);
                binCounts[bin]++;
                sumB[bin] += sourcePixels[offset];
                sumG[bin] += sourcePixels[offset + 1];
                sumR[bin] += sourcePixels[offset + 2];
                errorScores[bin] += weightedError;
            }
        }

        int minimumBinPixels = PatchDetailMinimumBinPixels(width, height, quality, patchDetail);
        int[] selectedBins = Enumerable.Range(0, binCount)
            .Where(bin => binCounts[bin] >= minimumBinPixels)
            .OrderByDescending(bin => errorScores[bin])
            .Take(MaxPatchDetailColorBinsForRegion(patchDetail))
            .ToArray();

        if (selectedBins.Length == 0)
        {
            return new List<(RegionShape Shape, double Score)>();
        }

        Color[] binColors = BuildColorsFromStats(binCounts, sumB, sumG, sumR);
        int[] selectedIndexByBin = Enumerable.Repeat(-1, binCount).ToArray();
        byte[][] masks = new byte[selectedBins.Length][];
        for (int i = 0; i < selectedBins.Length; i++)
        {
            selectedIndexByBin[selectedBins[i]] = i;
            masks[i] = new byte[clip.Width * clip.Height];
        }

        for (int y = top; y < bottomExclusive; y++)
        {
            int row = y * width;
            int maskRow = (y - top) * maskWidth;
            for (int x = left; x < rightExclusive; x++)
            {
                int pixel = row + x;
                int maskIndex = maskRow + x - left;
                if (regionMask[maskIndex] == 0)
                {
                    continue;
                }

                int offset = pixel * 3;
                bool isForeground = IsForegroundPixel(foregroundMask, pixel);
                double effectiveThreshold = PatchDetailEffectiveThreshold(errorThreshold, objectFocus, isForeground, foregroundMask is not null);
                if (!PredictedBelongsToRegion(predictedPixels, offset, region.Fill, ownerThreshold) ||
                    PixelColorDistance(sourcePixels, predictedPixels, offset) < effectiveThreshold)
                {
                    continue;
                }

                int selectedIndex = selectedIndexByBin[QuantizeDetailColor(sourcePixels, offset)];
                if (selectedIndex >= 0)
                {
                    masks[selectedIndex][maskIndex] = 255;
                }
            }
        }

        List<(RegionShape Shape, double Score)> candidates = new();
        double minArea = PatchDetailRegionArea(width, height, quality, patchDetail);
        double simplify = PatchDetailSimplifyForQuality(quality, patchDetail);

        for (int selectedIndex = 0; selectedIndex < selectedBins.Length; selectedIndex++)
        {
            Color fill = binColors[selectedBins[selectedIndex]];
            double averageError = errorScores[selectedBins[selectedIndex]] / Math.Max(1, binCounts[selectedBins[selectedIndex]]);
            candidates.AddRange(TracePatchDetailMask(masks[selectedIndex], fill, averageError, clip, minArea, simplify));
        }

        return candidates;
    }

    private static byte[] BuildRegionMask(RegionShape region, Rectangle clip)
    {
        using Mat mask = new(clip.Height, clip.Width, DepthType.Cv8U, 1);
        mask.SetTo(new MCvScalar(0));
        Point[] shiftedPoints = region.Points
            .Select(point => new Point(point.X - clip.Left, point.Y - clip.Top))
            .ToArray();
        using VectorOfPoint polygon = new(shiftedPoints);
        using VectorOfVectorOfPoint polygons = new();
        polygons.Push(polygon);
        CvInvoke.FillPoly(mask, polygons, new MCvScalar(255), LineType.EightConnected);

        byte[] maskPixels = new byte[clip.Width * clip.Height];
        Marshal.Copy(mask.DataPointer, maskPixels, 0, maskPixels.Length);
        return maskPixels;
    }

    private static List<(RegionShape Shape, double Score)> TracePatchDetailMask(byte[] maskBytes, Color fill, double averageError, Rectangle clip, double minArea, double simplify)
    {
        List<(RegionShape Shape, double Score)> candidates = new();
        using Mat mask = new(clip.Height, clip.Width, DepthType.Cv8U, 1);
        Marshal.Copy(maskBytes, 0, mask.DataPointer, maskBytes.Length);

        using Mat cleanedMask = CleanPatchDetailMask(mask);
        using VectorOfVectorOfPoint contours = new();
        using Mat hierarchy = new();
        CvInvoke.FindContours(cleanedMask, contours, hierarchy, RetrType.External, ChainApproxMethod.ChainApproxSimple);

        for (int i = 0; i < contours.Size; i++)
        {
            using VectorOfPoint contour = contours[i];
            double area = Math.Abs(CvInvoke.ContourArea(contour));
            if (area < minArea)
            {
                continue;
            }

            using VectorOfPoint simplified = new();
            CvInvoke.ApproxPolyDP(contour, simplified, simplify, true);
            Point[] points = simplified.ToArray();
            if (points.Length < 3)
            {
                continue;
            }

            Rectangle bounds = CvInvoke.BoundingRectangle(simplified);
            if (bounds.Width <= 2 || bounds.Height <= 2)
            {
                continue;
            }

            List<Point> absolutePoints = points
                .Select(point => new Point(point.X + clip.Left, point.Y + clip.Top))
                .ToList();
            Rectangle absoluteBounds = Rectangle.FromLTRB(
                bounds.Left + clip.Left,
                bounds.Top + clip.Top,
                bounds.Right + clip.Left,
                bounds.Bottom + clip.Top);

            RegionShape patch = new()
            {
                Id = 0,
                Fill = fill,
                Points = absolutePoints,
                Area = area,
                Center = AveragePoint(absolutePoints),
                Bounds = absoluteBounds,
                IsCorrection = true
            };
            candidates.Add((patch, area * averageError));
        }

        return candidates;
    }

    private static Mat CleanPatchDetailMask(Mat detailMask)
    {
        using Mat opened = new();
        using Mat closed = new();
        Mat cleaned = new();
        using Mat kernel = CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(3, 3), new Point(-1, -1));

        CvInvoke.MorphologyEx(detailMask, opened, MorphOp.Open, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.MorphologyEx(opened, closed, MorphOp.Close, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.MorphologyEx(closed, cleaned, MorphOp.Open, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        return cleaned;
    }

    private static bool PredictedBelongsToRegion(byte[] predictedPixels, int offset, Color fill, double threshold)
    {
        double db = predictedPixels[offset] - fill.B;
        double dg = predictedPixels[offset + 1] - fill.G;
        double dr = predictedPixels[offset + 2] - fill.R;
        return Math.Sqrt(db * db + dg * dg + dr * dr) <= threshold;
    }

    private static double PixelColorDistance(byte[] a, byte[] b, int offset)
    {
        double db = a[offset] - b[offset];
        double dg = a[offset + 1] - b[offset + 1];
        double dr = a[offset + 2] - b[offset + 2];
        return Math.Sqrt(db * db + dg * dg + dr * dr);
    }

    private static int PixelDistanceSquared(byte[] pixels, int offsetA, int offsetB)
    {
        int db = pixels[offsetA] - pixels[offsetB];
        int dg = pixels[offsetA + 1] - pixels[offsetB + 1];
        int dr = pixels[offsetA + 2] - pixels[offsetB + 2];
        return db * db + dg * dg + dr * dr;
    }

    private static int Square(int value)
    {
        return value * value;
    }

    private static bool IsForegroundPixel(byte[]? foregroundMask, int pixel)
    {
        return foregroundMask is not null && pixel >= 0 && pixel < foregroundMask.Length && foregroundMask[pixel] != 0;
    }

    private static double PatchDetailEffectiveThreshold(double baseThreshold, int objectFocus, bool isForeground, bool hasForegroundMask)
    {
        if (!hasForegroundMask || objectFocus <= 0)
        {
            return baseThreshold;
        }

        double factor = isForeground
            ? Math.Clamp(1.0 - objectFocus * 0.0045, 0.55, 1.0)
            : Math.Clamp(1.0 + objectFocus * 0.009, 1.0, 1.9);
        return baseThreshold * factor;
    }

    private static double PatchDetailWeightedError(double error, int objectFocus, bool isForeground, bool hasForegroundMask)
    {
        if (!hasForegroundMask || objectFocus <= 0)
        {
            return error;
        }

        double factor = isForeground
            ? 1.0 + objectFocus / 32.0
            : Math.Clamp(1.0 - objectFocus / 150.0, 0.35, 1.0);
        return error * factor;
    }

    private static int QuantizeDetailColor(byte[] pixels, int offset)
    {
        int b = pixels[offset] >> 4;
        int g = pixels[offset + 1] >> 4;
        int r = pixels[offset + 2] >> 4;
        return (r << 8) | (g << 4) | b;
    }

    private static List<RegionShape> TraceErrorRegions(Mat source, byte[] sourcePixels, Color background, List<RegionShape> baseRegions, int quality, int darkFilter, int correctionStrength, int workerCount)
    {
        int width = source.Width;
        int height = source.Height;
        int pixelCount = checked(width * height);
        int workers = Math.Max(1, workerCount);

        using Mat prediction = RenderRegionFrame(baseRegions, background, null, width, height);
        byte[] predictedPixels = new byte[sourcePixels.Length];
        Marshal.Copy(prediction.DataPointer, predictedPixels, 0, predictedPixels.Length);

        int threshold = ErrorThresholdForQuality(quality, correctionStrength);
        ushort[] residualBins = new ushort[pixelCount];
        const int binCount = 512;
        int[][] localCounts = new int[workers][];
        long[][] localSumB = new long[workers][];
        long[][] localSumG = new long[workers][];
        long[][] localSumR = new long[workers][];

        Parallel.For(0, workers, worker =>
        {
            int start = pixelCount * worker / workers;
            int end = pixelCount * (worker + 1) / workers;
            int[] counts = new int[binCount];
            long[] sumB = new long[binCount];
            long[] sumG = new long[binCount];
            long[] sumR = new long[binCount];

            for (int pixel = start; pixel < end; pixel++)
            {
                int offset = pixel * 3;
                int db = Math.Abs(sourcePixels[offset] - predictedPixels[offset]);
                int dg = Math.Abs(sourcePixels[offset + 1] - predictedPixels[offset + 1]);
                int dr = Math.Abs(sourcePixels[offset + 2] - predictedPixels[offset + 2]);
                int maxChannel = Math.Max(db, Math.Max(dg, dr));
                int total = db + dg + dr;

                if (maxChannel < threshold && total < threshold * 2)
                {
                    continue;
                }

                int bin = QuantizeResidualColor(sourcePixels, offset);
                residualBins[pixel] = (ushort)(bin + 1);
                counts[bin]++;
                sumB[bin] += sourcePixels[offset];
                sumG[bin] += sourcePixels[offset + 1];
                sumR[bin] += sourcePixels[offset + 2];
            }

            localCounts[worker] = counts;
            localSumB[worker] = sumB;
            localSumG[worker] = sumG;
            localSumR[worker] = sumR;
        });

        int[] binCounts = new int[binCount];
        long[] binSumB = new long[binCount];
        long[] binSumG = new long[binCount];
        long[] binSumR = new long[binCount];

        for (int worker = 0; worker < workers; worker++)
        {
            for (int bin = 0; bin < binCount; bin++)
            {
                binCounts[bin] += localCounts[worker][bin];
                binSumB[bin] += localSumB[worker][bin];
                binSumG[bin] += localSumG[worker][bin];
                binSumR[bin] += localSumR[worker][bin];
            }
        }

        int minimumBinPixels = Math.Max(8, (int)Math.Round(ErrorRegionArea(width, height, quality, correctionStrength) * 0.7));
        int[] selectedBins = Enumerable.Range(0, binCount)
            .Where(bin => binCounts[bin] >= minimumBinPixels)
            .OrderByDescending(bin => binCounts[bin])
            .Take(MaxErrorColorBinsForQuality(quality, correctionStrength))
            .ToArray();

        if (selectedBins.Length == 0)
        {
            return new List<RegionShape>();
        }

        Color[] binColors = BuildColorsFromStats(binCounts, binSumB, binSumG, binSumR);
        int[] selectedIndexByBin = Enumerable.Repeat(-1, binCount).ToArray();
        byte[][] masks = new byte[selectedBins.Length][];
        for (int i = 0; i < selectedBins.Length; i++)
        {
            selectedIndexByBin[selectedBins[i]] = i;
            masks[i] = new byte[pixelCount];
        }

        Parallel.For(0, workers, worker =>
        {
            int start = pixelCount * worker / workers;
            int end = pixelCount * (worker + 1) / workers;
            for (int pixel = start; pixel < end; pixel++)
            {
                int packedBin = residualBins[pixel];
                if (packedBin == 0)
                {
                    continue;
                }

                int selectedIndex = selectedIndexByBin[packedBin - 1];
                if (selectedIndex >= 0)
                {
                    masks[selectedIndex][pixel] = 255;
                }
            }
        });

        double minArea = ErrorRegionArea(width, height, quality, correctionStrength);
        double simplify = ErrorSimplifyForQuality(quality);
        List<RegionShape> corrections = new();
        object correctionsLock = new();

        Parallel.For(
            0,
            selectedBins.Length,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            () => new List<RegionShape>(),
            (selectedIndex, _, localCorrections) =>
        {
            byte[] maskBytes = masks[selectedIndex];
            using Mat colorMask = new(height, width, DepthType.Cv8U, 1);
            Marshal.Copy(maskBytes, 0, colorMask.DataPointer, maskBytes.Length);

            using Mat cleanedMask = CleanResidualColorMask(colorMask);
            using VectorOfVectorOfPoint contours = new();
            using Mat hierarchy = new();
            CvInvoke.FindContours(cleanedMask, contours, hierarchy, RetrType.External, ChainApproxMethod.ChainApproxSimple);
            Color fill = binColors[selectedBins[selectedIndex]];

            for (int i = 0; i < contours.Size; i++)
            {
                using VectorOfPoint contour = contours[i];
                double area = Math.Abs(CvInvoke.ContourArea(contour));
                if (area < minArea)
                {
                    continue;
                }

                using VectorOfPoint simplified = new();
                CvInvoke.ApproxPolyDP(contour, simplified, simplify, true);
                Point[] points = simplified.ToArray();
                if (points.Length < 3)
                {
                    continue;
                }

                Rectangle bounds = CvInvoke.BoundingRectangle(simplified);
                if (IsLikelyResidualNoise(fill, area, bounds, minArea, darkFilter))
                {
                    continue;
                }

                localCorrections.Add(new RegionShape
                {
                    Id = 0,
                    Fill = fill,
                    Points = points.ToList(),
                    Area = area,
                    Center = AveragePoint(points),
                    Bounds = bounds,
                    IsCorrection = true
                });
            }

            return localCorrections;
        },
            localCorrections =>
            {
                if (localCorrections.Count == 0)
                {
                    return;
                }

                lock (correctionsLock)
                {
                    corrections.AddRange(localCorrections);
                }
            });

        return corrections
            .OrderByDescending(region => region.Area)
            .Take(MaxErrorRegionsForQuality(quality, correctionStrength))
            .ToList();
    }

    private static int QuantizeResidualColor(byte[] pixels, int offset)
    {
        int b = pixels[offset] >> 5;
        int g = pixels[offset + 1] >> 5;
        int r = pixels[offset + 2] >> 5;
        return (r << 6) | (g << 3) | b;
    }

    private static Mat CleanResidualColorMask(Mat errorMask)
    {
        using Mat opened = new();
        using Mat closed = new();
        Mat cleaned = new();
        using Mat kernel = CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(3, 3), new Point(-1, -1));

        CvInvoke.MorphologyEx(errorMask, opened, MorphOp.Open, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.MorphologyEx(opened, closed, MorphOp.Close, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.MorphologyEx(closed, cleaned, MorphOp.Open, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        return cleaned;
    }

    private static bool IsLikelyResidualNoise(Color fill, double area, Rectangle bounds, double minArea, int darkFilter)
    {
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return true;
        }

        double boundsArea = Math.Max(1, bounds.Width * bounds.Height);
        double compactness = area / boundsArea;
        if (compactness < 0.08)
        {
            return true;
        }

        if (darkFilter > 0 && Luminance(fill) < DarkLuminanceThreshold(darkFilter) && area < minArea * DarkResidualAreaFactor(darkFilter))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyDarkSpeckle(Color fill, double area, Rectangle bounds, double minArea, double frameArea, int darkFilter)
    {
        if (Luminance(fill) >= DarkLuminanceThreshold(darkFilter))
        {
            return false;
        }

        double boundsArea = Math.Max(1, bounds.Width * bounds.Height);
        double compactness = area / boundsArea;
        int thinSide = Math.Min(bounds.Width, bounds.Height);
        int longSide = Math.Max(bounds.Width, bounds.Height);
        double elongation = longSide / (double)Math.Max(1, thinSide);
        double areaLimit = minArea * DarkBaseAreaFactor(darkFilter);

        if (area >= Math.Max(areaLimit, frameArea * 0.004))
        {
            return false;
        }

        if (thinSide <= 3 && area < areaLimit)
        {
            return true;
        }

        if (elongation >= 8 && area < areaLimit)
        {
            return true;
        }

        if (compactness < 0.18 && area < areaLimit)
        {
            return true;
        }

        if (compactness < 0.1 && area < areaLimit * 1.7)
        {
            return true;
        }

        return false;
    }

    private static double DarkLuminanceThreshold(int darkFilter)
    {
        return 12 + darkFilter * 0.45;
    }

    private static double DarkBaseAreaFactor(int darkFilter)
    {
        return 1.5 + darkFilter * 0.095;
    }

    private static double DarkResidualAreaFactor(int darkFilter)
    {
        return 1.8 + darkFilter * 0.08;
    }

    private static byte[][] BuildLabelMasks(byte[] labels, int[] counts, int pixelCount, int workerCount)
    {
        long activeLabels = counts.LongCount(count => count > 0);
        long estimatedBytes = activeLabels * pixelCount;
        const long maxMaskCacheBytes = 512L * 1024 * 1024;
        int workers = Math.Max(1, workerCount);

        if (estimatedBytes > maxMaskCacheBytes)
        {
            return BuildLabelMasksConservatively(labels, counts, pixelCount, workers);
        }

        byte[][] masks = new byte[counts.Length][];
        for (int label = 0; label < counts.Length; label++)
        {
            masks[label] = counts[label] == 0 ? Array.Empty<byte>() : new byte[pixelCount];
        }

        Parallel.For(
            0,
            workers,
            worker =>
            {
                int start = pixelCount * worker / workers;
                int end = pixelCount * (worker + 1) / workers;

                for (int pixel = start; pixel < end; pixel++)
                {
                    masks[labels[pixel]][pixel] = 255;
                }
            });

        return masks;
    }

    private static byte[][] BuildLabelMasksConservatively(byte[] labels, int[] counts, int pixelCount, int workerCount)
    {
        byte[][] masks = new byte[counts.Length][];
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, workerCount) };

        Parallel.For(0, counts.Length, options, label =>
        {
            if (counts[label] == 0)
            {
                masks[label] = Array.Empty<byte>();
                return;
            }

            byte[] mask = new byte[pixelCount];
            for (int pixel = 0; pixel < labels.Length; pixel++)
            {
                if (labels[pixel] == label)
                {
                    mask[pixel] = 255;
                }
            }

            masks[label] = mask;
        });

        return masks;
    }

    private static Color SelectBackgroundColor(Color[] colors, int[] counts)
    {
        int bestIndex = 0;
        int bestCount = 0;
        int bestNonDarkIndex = -1;
        int bestNonDarkCount = 0;
        long totalCount = 0;
        long nonDarkCount = 0;

        for (int i = 0; i < counts.Length; i++)
        {
            totalCount += counts[i];
            if (counts[i] > bestCount)
            {
                bestCount = counts[i];
                bestIndex = i;
            }

            if (Luminance(colors[i]) >= 34)
            {
                nonDarkCount += counts[i];
                if (counts[i] > bestNonDarkCount)
                {
                    bestNonDarkCount = counts[i];
                    bestNonDarkIndex = i;
                }
            }
        }

        if (colors.Length == 0)
        {
            return Color.Black;
        }

        if (bestNonDarkIndex >= 0 &&
            nonDarkCount >= totalCount * 0.18 &&
            bestNonDarkCount >= totalCount * 0.03)
        {
            return colors[bestNonDarkIndex];
        }

        return colors[bestIndex];
    }

    private static Backdrop BuildBackdrop(byte[] pixels, int width, int height, int quality)
    {
        int columns = BackdropColumnsForQuality(width, quality);
        int rows = Math.Clamp((int)Math.Round(columns * height / (double)Math.Max(1, width)), 1, Math.Max(1, height));
        Color[] colors = new Color[columns * rows];

        Parallel.For(0, rows, y =>
        {
            int y0 = y * height / rows;
            int y1 = Math.Max(y0 + 1, (y + 1) * height / rows);

            for (int x = 0; x < columns; x++)
            {
                int x0 = x * width / columns;
                int x1 = Math.Max(x0 + 1, (x + 1) * width / columns);
                long sumB = 0;
                long sumG = 0;
                long sumR = 0;
                int count = 0;

                for (int sourceY = y0; sourceY < y1; sourceY++)
                {
                    int rowOffset = sourceY * width * 3;
                    for (int sourceX = x0; sourceX < x1; sourceX++)
                    {
                        int offset = rowOffset + sourceX * 3;
                        sumB += pixels[offset];
                        sumG += pixels[offset + 1];
                        sumR += pixels[offset + 2];
                        count++;
                    }
                }

                int index = y * columns + x;
                colors[index] = count == 0
                    ? Color.Black
                    : Color.FromArgb(
                        ClampByte((int)Math.Round(sumR / (double)count)),
                        ClampByte((int)Math.Round(sumG / (double)count)),
                        ClampByte((int)Math.Round(sumB / (double)count)));
            }
        });

        return new Backdrop(columns, rows, colors);
    }

    private static int BackdropColumnsForQuality(int width, int quality)
    {
        int target = quality >= 92 ? 64 : quality >= 70 ? 48 : 32;
        return Math.Clamp(target, 1, Math.Max(1, width));
    }

    private static Palette BuildPalette(byte[] pixels, int width, int height, int requestedPaletteSize, Palette? seed)
    {
        List<int> samples = SamplePixelOffsets(width, height);
        int paletteSize = Math.Min(requestedPaletteSize, samples.Count);
        if (paletteSize <= 0)
        {
            throw new InvalidOperationException("No pixels available for palette building.");
        }

        bool useSeed = seed is not null && seed.Size == paletteSize;
        double[] centerB = useSeed ? seed!.B.ToArray() : new double[paletteSize];
        double[] centerG = useSeed ? seed!.G.ToArray() : new double[paletteSize];
        double[] centerR = useSeed ? seed!.R.ToArray() : new double[paletteSize];

        if (!useSeed)
        {
            for (int i = 0; i < paletteSize; i++)
            {
                int sampleIndex = samples[(int)Math.Round(i * (samples.Count - 1) / Math.Max(1.0, paletteSize - 1.0))];
                int offset = sampleIndex * 3;
                centerB[i] = pixels[offset];
                centerG[i] = pixels[offset + 1];
                centerR[i] = pixels[offset + 2];
            }
        }

        int[] counts = new int[paletteSize];
        double[] sumB = new double[paletteSize];
        double[] sumG = new double[paletteSize];
        double[] sumR = new double[paletteSize];

        int iterations = useSeed ? 3 : 8;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(counts);
            Array.Clear(sumB);
            Array.Clear(sumG);
            Array.Clear(sumR);

            foreach (int sampleIndex in samples)
            {
                int offset = sampleIndex * 3;
                int nearest = FindNearestPaletteColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], centerB, centerG, centerR);
                counts[nearest]++;
                sumB[nearest] += pixels[offset];
                sumG[nearest] += pixels[offset + 1];
                sumR[nearest] += pixels[offset + 2];
            }

            for (int i = 0; i < paletteSize; i++)
            {
                if (counts[i] == 0)
                {
                    continue;
                }

                centerB[i] = sumB[i] / counts[i];
                centerG[i] = sumG[i] / counts[i];
                centerR[i] = sumR[i] / counts[i];
            }
        }

        return new Palette(centerB, centerG, centerR);
    }

    private static byte[] AssignPaletteLabels(byte[] pixels, Palette palette, AccelerationOptions acceleration, out Color[] colors, out int[] counts)
    {
        if (acceleration.CudaLabeler is not null)
        {
            try
            {
                byte[] cudaLabels = acceleration.CudaLabeler.AssignPaletteLabels(pixels, palette);
                BuildPaletteStats(pixels, cudaLabels, palette.Size, out colors, out counts);
                return cudaLabels;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CUDA label assignment failed, falling back to CPU: {ex.Message}");
            }
        }

        return AssignPaletteLabelsCpu(pixels, palette, out colors, out counts);
    }

    private static byte[] AssignPaletteLabelsCpu(byte[] pixels, Palette palette, out Color[] colors, out int[] counts)
    {
        int pixelCount = pixels.Length / 3;
        byte[] labels = new byte[pixelCount];
        int workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, pixelCount / 8192 + 1));
        int[][] localCounts = new int[workerCount][];
        long[][] localSumB = new long[workerCount][];
        long[][] localSumG = new long[workerCount][];
        long[][] localSumR = new long[workerCount][];

        Parallel.For(0, workerCount, worker =>
        {
            int start = pixelCount * worker / workerCount;
            int end = pixelCount * (worker + 1) / workerCount;
            int[] workerCounts = new int[palette.Size];
            long[] workerSumB = new long[palette.Size];
            long[] workerSumG = new long[palette.Size];
            long[] workerSumR = new long[palette.Size];

            for (int pixel = start; pixel < end; pixel++)
            {
                int offset = pixel * 3;
                int nearest = FindNearestPaletteColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], palette.B, palette.G, palette.R);
                labels[pixel] = (byte)nearest;
                workerCounts[nearest]++;
                workerSumB[nearest] += pixels[offset];
                workerSumG[nearest] += pixels[offset + 1];
                workerSumR[nearest] += pixels[offset + 2];
            }

            localCounts[worker] = workerCounts;
            localSumB[worker] = workerSumB;
            localSumG[worker] = workerSumG;
            localSumR[worker] = workerSumR;
        });

        counts = new int[palette.Size];
        long[] sumB = new long[palette.Size];
        long[] sumG = new long[palette.Size];
        long[] sumR = new long[palette.Size];

        for (int worker = 0; worker < workerCount; worker++)
        {
            for (int label = 0; label < palette.Size; label++)
            {
                counts[label] += localCounts[worker][label];
                sumB[label] += localSumB[worker][label];
                sumG[label] += localSumG[worker][label];
                sumR[label] += localSumR[worker][label];
            }
        }

        colors = BuildColorsFromStats(counts, sumB, sumG, sumR);
        return labels;
    }

    private static void BuildPaletteStats(byte[] pixels, byte[] labels, int paletteSize, out Color[] colors, out int[] counts)
    {
        int pixelCount = pixels.Length / 3;
        int workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, pixelCount / 8192 + 1));
        int[][] localCounts = new int[workerCount][];
        long[][] localSumB = new long[workerCount][];
        long[][] localSumG = new long[workerCount][];
        long[][] localSumR = new long[workerCount][];

        Parallel.For(0, workerCount, worker =>
        {
            int start = pixelCount * worker / workerCount;
            int end = pixelCount * (worker + 1) / workerCount;
            int[] workerCounts = new int[paletteSize];
            long[] workerSumB = new long[paletteSize];
            long[] workerSumG = new long[paletteSize];
            long[] workerSumR = new long[paletteSize];

            for (int pixel = start; pixel < end; pixel++)
            {
                int offset = pixel * 3;
                int label = labels[pixel];
                workerCounts[label]++;
                workerSumB[label] += pixels[offset];
                workerSumG[label] += pixels[offset + 1];
                workerSumR[label] += pixels[offset + 2];
            }

            localCounts[worker] = workerCounts;
            localSumB[worker] = workerSumB;
            localSumG[worker] = workerSumG;
            localSumR[worker] = workerSumR;
        });

        counts = new int[paletteSize];
        long[] sumB = new long[paletteSize];
        long[] sumG = new long[paletteSize];
        long[] sumR = new long[paletteSize];

        for (int worker = 0; worker < workerCount; worker++)
        {
            for (int label = 0; label < paletteSize; label++)
            {
                counts[label] += localCounts[worker][label];
                sumB[label] += localSumB[worker][label];
                sumG[label] += localSumG[worker][label];
                sumR[label] += localSumR[worker][label];
            }
        }

        colors = BuildColorsFromStats(counts, sumB, sumG, sumR);
    }

    private static Color[] BuildColorsFromStats(int[] counts, long[] sumB, long[] sumG, long[] sumR)
    {
        Color[] colors = new Color[counts.Length];
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                colors[i] = Color.Black;
                continue;
            }

            int b = (int)Math.Round((double)sumB[i] / counts[i]);
            int g = (int)Math.Round((double)sumG[i] / counts[i]);
            int r = (int)Math.Round((double)sumR[i] / counts[i]);
            colors[i] = Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
        }

        return colors;
    }

    private static int FindNearestPaletteColor(byte b, byte g, byte r, double[] centerB, double[] centerG, double[] centerR)
    {
        return FindNearestPaletteColor((double)b, g, r, centerB, centerG, centerR);
    }

    private static int FindNearestPaletteColor(double b, double g, double r, double[] centerB, double[] centerG, double[] centerR)
    {
        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < centerB.Length; i++)
        {
            double db = b - centerB[i];
            double dg = g - centerG[i];
            double dr = r - centerR[i];
            double distance = db * db + dg * dg + dr * dr;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static List<int> SamplePixelOffsets(int width, int height)
    {
        int totalPixels = checked(width * height);
        int stride = Math.Max(1, (int)Math.Sqrt((double)totalPixels / MaxKMeansSamples));
        List<int> samples = new(Math.Min(totalPixels, MaxKMeansSamples + width + height));

        for (int y = 0; y < height; y += stride)
        {
            for (int x = 0; x < width; x += stride)
            {
                samples.Add(y * width + x);
            }
        }

        if (samples.Count == 0)
        {
            samples.Add(0);
        }

        return samples;
    }

    private static void AssignRegionIds(List<RegionShape> regions, List<RegionSignature> previousRegions, int width, int height, ref int nextRegionId)
    {
        double diagonal = Math.Sqrt(width * width + height * height);
        HashSet<int> usedPreviousIds = new();

        foreach (RegionShape region in regions.OrderByDescending(region => region.Area))
        {
            RegionSignature? best = null;
            double bestScore = double.MaxValue;

            foreach (RegionSignature previous in previousRegions)
            {
                if (usedPreviousIds.Contains(previous.Id))
                {
                    continue;
                }

                double score = MatchScore(region, previous, diagonal);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = previous;
                }
            }

            if (best is not null && bestScore <= 0.34)
            {
                region.Id = best.Id;
                usedPreviousIds.Add(best.Id);
            }
            else
            {
                region.Id = nextRegionId++;
            }
        }
    }

    private static double MatchScore(RegionShape current, RegionSignature previous, double diagonal)
    {
        double centerDistance = Distance(current.Center, previous.Center) / Math.Max(1, diagonal);
        double colorDistance = ColorDistance(current.Fill, previous.Fill) / 441.67295593;
        double areaRatio = Math.Abs(Math.Log((current.Area + 1) / (previous.Area + 1)));
        double areaScore = Math.Clamp(areaRatio / 2.0, 0, 1);
        double overlapScore = 1.0 - IntersectionOverUnion(current.Bounds, previous.Bounds);

        return centerDistance * 0.35 + colorDistance * 0.35 + areaScore * 0.15 + overlapScore * 0.15;
    }

    private static void ProcessVideoEdges(string videoPath, string outputPath, double sensitivity, bool highQuality, double simplify, AccelerationOptions acceleration, CompressionLevel compressionLevel)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file was not found.", videoPath);
        }

        VideoInfo info = ProbeVideo(videoPath);
        string pixelFormat = highQuality ? "bgr24" : "gray";
        int channels = highQuality ? 3 : 1;
        int frameSize = checked(info.Width * info.Height * channels);

        Console.WriteLine($"Encoding {videoPath}");
        Console.WriteLine($"Input: {info.Width}x{info.Height} @ {FormatDouble(info.Fps)} fps");
        Console.WriteLine($"Legacy edges: sensitivity {FormatDouble(sensitivity)}, simplify {FormatDouble(simplify)}, mode {(highQuality ? "high quality" : "fast gray")}");
        Console.WriteLine($"Acceleration: {DescribeAcceleration(acceleration)}");
        Console.WriteLine($"Compression: {DescribeCompression(compressionLevel)}");

        ConfigureAcceleration(acceleration);

        using Process ffmpeg = StartFfmpegRawVideo(videoPath, pixelFormat, acceleration);
        byte[] buffer = new byte[frameSize];

        using Stream outputStream = OpenLvfWriteStream(outputPath, compressionLevel);
        using StreamWriter writer = new(outputStream, new UTF8Encoding(false));
        WriteEdgeHeader(writer, info);

        int frameCount = 0;
        long totalPaths = 0;
        long totalPoints = 0;

        while (ReadFullFrame(ffmpeg.StandardOutput.BaseStream, buffer))
        {
            using Mat frame = new(info.Height, info.Width, DepthType.Cv8U, channels);
            Marshal.Copy(buffer, 0, frame.DataPointer, frameSize);

            using Mat edges = DetectEdges(frame, sensitivity, highQuality);
            List<List<Point>> paths = ExtractEdgePaths(edges, simplify);

            writer.WriteLine($"FRAME {frameCount}");
            foreach (List<Point> path in paths)
            {
                writer.WriteLine(PathToString(path));
                totalPoints += path.Count;
            }
            writer.WriteLine("END");

            totalPaths += paths.Count;
            frameCount++;

            if (frameCount % 10 == 0)
            {
                Console.WriteLine($"Encoded {frameCount} frames | {totalPaths} paths | {totalPoints} points");
            }
        }

        ffmpeg.WaitForExit();
        if (ffmpeg.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}.");
        }

        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine($"Frames: {frameCount}, paths: {totalPaths}, points: {totalPoints}");
    }

    private static Mat DetectEdges(Mat frame, double sensitivity, bool highQuality)
    {
        Mat edges = new();
        double lower = Math.Max(1, sensitivity);
        double upper = Math.Max(lower + 1, sensitivity * 2);

        if (highQuality && frame.NumberOfChannels == 3)
        {
            using Mat lab = new();
            CvInvoke.CvtColor(frame, lab, ColorConversion.Bgr2Lab);

            using VectorOfMat channels = new();
            CvInvoke.Split(lab, channels);
            using Mat luminance = channels[0];
            CvInvoke.Canny(luminance, edges, lower, upper);
        }
        else if (frame.NumberOfChannels == 1)
        {
            CvInvoke.Canny(frame, edges, lower, upper);
        }
        else
        {
            using Mat gray = new();
            CvInvoke.CvtColor(frame, gray, ColorConversion.Bgr2Gray);
            CvInvoke.Canny(gray, edges, lower, upper);
        }

        return edges;
    }

    private static List<List<Point>> ExtractEdgePaths(Mat edges, double simplify)
    {
        using VectorOfVectorOfPoint contours = new();
        using Mat hierarchy = new();
        CvInvoke.FindContours(edges, contours, hierarchy, RetrType.List, ChainApproxMethod.ChainApproxSimple);

        List<List<Point>> paths = new(contours.Size);
        for (int i = 0; i < contours.Size; i++)
        {
            using VectorOfPoint contour = contours[i];
            if (contour.Size == 0)
            {
                continue;
            }

            VectorOfPoint? simplified = null;
            try
            {
                VectorOfPoint vector = contour;
                if (simplify > 0 && contour.Size > 2)
                {
                    simplified = new VectorOfPoint();
                    CvInvoke.ApproxPolyDP(contour, simplified, simplify, false);
                    vector = simplified;
                }

                Point[] points = vector.ToArray();
                if (points.Length > 0)
                {
                    paths.Add(points.ToList());
                }
            }
            finally
            {
                simplified?.Dispose();
            }
        }

        return paths;
    }

    private static Mat RenderFrame(LvfFrame vectorFrame, int width, int height)
    {
        return vectorFrame.Regions.Count > 0
            ? RenderRegionFrame(vectorFrame.Regions, vectorFrame.Background, vectorFrame.Backdrop, width, height)
            : RenderEdgeFrame(vectorFrame.Paths, width, height);
    }

    private static GpuFrameData BuildGpuFrame(LvfFrame vectorFrame, int width, int height)
    {
        List<float> vertices = new(vectorFrame.Regions.Sum(region => Math.Max(0, region.Points.Count - 2)) * 15);

        if (vectorFrame.Backdrop is not null)
        {
            AppendBackdropTriangles(vertices, vectorFrame.Backdrop, width, height);
        }

        foreach (RegionShape region in vectorFrame.Regions)
        {
            AppendPolygonTriangles(vertices, region, width, height);
        }

        return new GpuFrameData(vectorFrame.Background, vertices.ToArray());
    }

    private static void AppendBackdropTriangles(List<float> vertices, Backdrop backdrop, int width, int height)
    {
        for (int y = 0; y < backdrop.Rows; y++)
        {
            float top = y * height / (float)backdrop.Rows;
            float bottom = (y + 1) * height / (float)backdrop.Rows;

            for (int x = 0; x < backdrop.Columns; x++)
            {
                float left = x * width / (float)backdrop.Columns;
                float right = (x + 1) * width / (float)backdrop.Columns;
                Color fill = backdrop.Colors[y * backdrop.Columns + x];

                AddGpuVertex(vertices, left, top, fill, width, height);
                AddGpuVertex(vertices, right, top, fill, width, height);
                AddGpuVertex(vertices, right, bottom, fill, width, height);
                AddGpuVertex(vertices, left, top, fill, width, height);
                AddGpuVertex(vertices, right, bottom, fill, width, height);
                AddGpuVertex(vertices, left, bottom, fill, width, height);
            }
        }
    }

    private static void AppendPolygonTriangles(List<float> vertices, RegionShape region, int width, int height)
    {
        if (region.Points.Count < 3)
        {
            return;
        }

        try
        {
            Tess tess = new();
            ContourVertex[] contour = region.Points
                .Select(point => new ContourVertex { Position = new Vec3(point.X, point.Y, 0) })
                .ToArray();

            tess.AddContour(contour, ContourOrientation.Original);
            tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3);

            if (tess.ElementCount > 0)
            {
                for (int element = 0; element < tess.ElementCount; element++)
                {
                    int baseIndex = element * 3;
                    int a = tess.Elements[baseIndex];
                    int b = tess.Elements[baseIndex + 1];
                    int c = tess.Elements[baseIndex + 2];
                    if (a < 0 || b < 0 || c < 0)
                    {
                        continue;
                    }

                    Vec3 pa = tess.Vertices[a].Position;
                    Vec3 pb = tess.Vertices[b].Position;
                    Vec3 pc = tess.Vertices[c].Position;
                    AddGpuVertex(vertices, pa.X, pa.Y, region.Fill, width, height);
                    AddGpuVertex(vertices, pb.X, pb.Y, region.Fill, width, height);
                    AddGpuVertex(vertices, pc.X, pc.Y, region.Fill, width, height);
                }

                return;
            }
        }
        catch
        {
            // Fall back to a simple fan for rare malformed polygons.
        }

        Point origin = region.Points[0];
        for (int i = 1; i + 1 < region.Points.Count; i++)
        {
            AddGpuVertex(vertices, origin.X, origin.Y, region.Fill, width, height);
            AddGpuVertex(vertices, region.Points[i].X, region.Points[i].Y, region.Fill, width, height);
            AddGpuVertex(vertices, region.Points[i + 1].X, region.Points[i + 1].Y, region.Fill, width, height);
        }
    }

    private static void AddGpuVertex(List<float> vertices, float x, float y, Color fill, int width, int height)
    {
        vertices.Add(x / Math.Max(1, width) * 2f - 1f);
        vertices.Add(1f - y / Math.Max(1, height) * 2f);
        vertices.Add(fill.R / 255f);
        vertices.Add(fill.G / 255f);
        vertices.Add(fill.B / 255f);
    }

    private static Mat RenderRegionFrame(List<RegionShape> regions, Color background, Backdrop? backdrop, int width, int height)
    {
        Mat frame = backdrop is null
            ? new Mat(height, width, DepthType.Cv8U, 3)
            : RenderBackdrop(backdrop, width, height);

        if (backdrop is null)
        {
            frame.SetTo(new MCvScalar(background.B, background.G, background.R));
        }

        foreach (RegionShape region in regions)
        {
            if (region.Points.Count < 3)
            {
                continue;
            }

            using VectorOfPoint polygon = new(region.Points.ToArray());
            using VectorOfVectorOfPoint polygons = new();
            polygons.Push(polygon);
            CvInvoke.FillPoly(frame, polygons, new MCvScalar(region.Fill.B, region.Fill.G, region.Fill.R), LineType.EightConnected);
        }

        return frame;
    }

    private static Mat RenderBackdrop(Backdrop backdrop, int width, int height)
    {
        byte[] pixels = new byte[backdrop.Columns * backdrop.Rows * 3];
        for (int i = 0; i < backdrop.Colors.Length; i++)
        {
            Color color = backdrop.Colors[i];
            int offset = i * 3;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
        }

        using Mat small = new(backdrop.Rows, backdrop.Columns, DepthType.Cv8U, 3);
        Marshal.Copy(pixels, 0, small.DataPointer, pixels.Length);

        Mat frame = new(height, width, DepthType.Cv8U, 3);
        CvInvoke.Resize(small, frame, new Size(width, height), 0, 0, Inter.Linear);
        return frame;
    }

    private static Mat RenderEdgeFrame(List<List<Point>> paths, int width, int height)
    {
        Mat frame = new(height, width, DepthType.Cv8U, 1);
        frame.SetTo(new MCvScalar(0));

        foreach (List<Point> path in paths)
        {
            if (path.Count == 1)
            {
                CvInvoke.Circle(frame, path[0], 1, new MCvScalar(255), -1, LineType.AntiAlias);
                continue;
            }

            for (int i = 1; i < path.Count; i++)
            {
                CvInvoke.Line(frame, path[i - 1], path[i], new MCvScalar(255), 1, LineType.AntiAlias);
            }
        }

        return frame;
    }

    private static void WriteRegionHeader(StreamWriter writer, VideoInfo info, int quality, int paletteSize)
    {
        writer.WriteLine("LVFVF2");
        writer.WriteLine($"FPS {FormatDouble(info.Fps)}");
        writer.WriteLine($"SIZE {info.Width} {info.Height}");
        writer.WriteLine($"QUALITY {quality}");
        writer.WriteLine($"PALETTE {paletteSize}");
    }

    private static void WriteEdgeHeader(StreamWriter writer, VideoInfo info)
    {
        writer.WriteLine("LVFVF1");
        writer.WriteLine($"FPS {FormatDouble(info.Fps)}");
        writer.WriteLine($"SIZE {info.Width} {info.Height}");
    }

    private static RegionFrameWriter CreateRegionFrameWriter(string outputPath, CompressionLevel compressionLevel)
    {
        Stream outputStream = OpenLvfWriteStream(outputPath, compressionLevel);
        return IsBinaryLvfPath(outputPath)
            ? new BinaryRegionFrameWriter(outputStream)
            : new TextRegionFrameWriter(outputStream);
    }

    private static Stream OpenLvfReadStream(string path)
    {
        FileStream fileStream = File.OpenRead(path);
        if (IsCompressedLvfPath(path))
        {
            return new BrotliStream(fileStream, CompressionMode.Decompress);
        }

        return fileStream;
    }

    private static Stream OpenLvfWriteStream(string path, CompressionLevel compressionLevel)
    {
        FileStream fileStream = File.Create(path);
        if (IsCompressedLvfPath(path))
        {
            return new BrotliStream(fileStream, compressionLevel);
        }

        return fileStream;
    }

    private static bool IsCompressedLvfPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".lvfz", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lvfb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lvfbz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinaryLvfPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".lvfb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lvfbz", StringComparison.OrdinalIgnoreCase);
    }

    private static LvfHeader ReadBinaryHeader(BinaryReader reader)
    {
        byte[] magic = reader.ReadBytes(BinaryRegionMagic.Length);
        if (magic.Length != BinaryRegionMagic.Length || !magic.SequenceEqual(BinaryRegionMagic))
        {
            throw new InvalidDataException("Unknown LVF binary format.");
        }

        byte version = reader.ReadByte();
        if (version is < 1 or > BinaryRegionVersion)
        {
            throw new InvalidDataException($"Unsupported LVF binary version: {version}");
        }

        int width = ReadVarInt(reader);
        int height = ReadVarInt(reader);
        double fps = reader.ReadDouble();
        int quality = ReadVarInt(reader);
        int paletteSize = ReadVarInt(reader);
        return new LvfHeader(fps, width, height, LvfFormat.RegionV2, quality, paletteSize, version);
    }

    private static LvfFrame? ReadNextBinaryRegionFrame(BinaryReader reader, int version)
    {
        byte marker;
        try
        {
            marker = reader.ReadByte();
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        if (marker != BinaryFrameMarker)
        {
            throw new InvalidDataException($"Invalid LVF binary frame marker: 0x{marker:X2}");
        }

        _ = ReadVarInt(reader);
        Color background = ReadBinaryColor(reader);
        Backdrop? backdrop = null;
        if (version == 1)
        {
            int backdropColumns = ReadVarInt(reader);
            int backdropRows = ReadVarInt(reader);
            int backdropColorCount = checked(backdropColumns * backdropRows);
            Color[] backdropColors = new Color[backdropColorCount];
            for (int i = 0; i < backdropColors.Length; i++)
            {
                backdropColors[i] = ReadBinaryColor(reader);
            }

            backdrop = new Backdrop(backdropColumns, backdropRows, backdropColors);
        }

        int regionCount = ReadVarInt(reader);
        List<RegionShape> regions = new(regionCount);
        for (int i = 0; i < regionCount; i++)
        {
            int id = ReadVarInt(reader);
            Color fill = ReadBinaryColor(reader);
            int pointCount = ReadVarInt(reader);
            List<Point> points = new(pointCount);
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                int x = ReadVarInt(reader);
                int y = ReadVarInt(reader);
                points.Add(new Point(x, y));
            }

            regions.Add(new RegionShape
            {
                Id = id,
                Fill = fill,
                Points = points,
                Area = Math.Abs(PolygonArea(points)),
                Center = AveragePoint(points),
                Bounds = BoundsFor(points),
                IsCorrection = id == 0
            });
        }

        return new LvfFrame(
            new List<List<Point>>(),
            regions,
            background,
            backdrop);
    }

    private static void WriteVarInt(BinaryWriter writer, int value)
    {
        if (value < 0)
        {
            throw new InvalidDataException($"Negative values are not supported in LVF binary data: {value}");
        }

        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            writer.Write((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        writer.Write((byte)remaining);
    }

    private static int ReadVarInt(BinaryReader reader)
    {
        int value = 0;
        int shift = 0;

        while (shift <= 28)
        {
            byte next = reader.ReadByte();
            value |= (next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("LVF binary varint is too large.");
    }

    private static void WriteBinaryColor(BinaryWriter writer, Color color)
    {
        writer.Write((byte)color.R);
        writer.Write((byte)color.G);
        writer.Write((byte)color.B);
    }

    private static Color ReadBinaryColor(BinaryReader reader)
    {
        int r = reader.ReadByte();
        int g = reader.ReadByte();
        int b = reader.ReadByte();
        return Color.FromArgb(r, g, b);
    }

    private static LvfHeader ReadHeader(StreamReader reader)
    {
        string firstLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Empty LVF file.");

        if (firstLine.Equals("LVFVF2", StringComparison.OrdinalIgnoreCase))
        {
            string fpsLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing FPS line.");
            string sizeLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing SIZE line.");
            string qualityLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing QUALITY line.");
            string paletteLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing PALETTE line.");

            (double fps, int width, int height) = ParseCommonHeader(fpsLine, sizeLine);
            int quality = ParseNamedInt(qualityLine, "QUALITY");
            int paletteSize = ParseNamedInt(paletteLine, "PALETTE");
            return new LvfHeader(fps, width, height, LvfFormat.RegionV2, quality, paletteSize);
        }

        if (firstLine.Equals("LVFVF1", StringComparison.OrdinalIgnoreCase))
        {
            string fpsLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing FPS line.");
            string sizeLine = ReadMeaningfulLine(reader) ?? throw new InvalidDataException("Missing SIZE line.");
            (double fps, int width, int height) = ParseCommonHeader(fpsLine, sizeLine);
            return new LvfHeader(fps, width, height, LvfFormat.EdgeV1, 0, 0);
        }

        string[] legacyParts = firstLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (legacyParts.Length == 3)
        {
            double fps = double.Parse(legacyParts[0], CultureInfo.InvariantCulture);
            int width = int.Parse(legacyParts[1], CultureInfo.InvariantCulture);
            int height = int.Parse(legacyParts[2], CultureInfo.InvariantCulture);
            return new LvfHeader(fps, width, height, LvfFormat.Legacy, 0, 0);
        }

        throw new InvalidDataException("Unknown LVF format.");
    }

    private static (double Fps, int Width, int Height) ParseCommonHeader(string fpsLine, string sizeLine)
    {
        string[] fpsParts = fpsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] sizeParts = sizeLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fpsParts.Length != 2 || !fpsParts[0].Equals("FPS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid FPS header line.");
        }

        if (sizeParts.Length != 3 || !sizeParts[0].Equals("SIZE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid SIZE header line.");
        }

        double fps = double.Parse(fpsParts[1], CultureInfo.InvariantCulture);
        int width = int.Parse(sizeParts[1], CultureInfo.InvariantCulture);
        int height = int.Parse(sizeParts[2], CultureInfo.InvariantCulture);
        return (fps, width, height);
    }

    private static int ParseNamedInt(string line, string name)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid {name} header line.");
        }

        return int.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    private static LvfFrame? ReadNextFrame(StreamReader reader, LvfHeader header)
    {
        return header.Format switch
        {
            LvfFormat.Legacy => ReadNextLegacyFrame(reader),
            LvfFormat.EdgeV1 => ReadNextEdgeFrame(reader),
            LvfFormat.RegionV2 => ReadNextRegionFrame(reader),
            _ => throw new InvalidDataException("Unsupported LVF format.")
        };
    }

    private static LvfFrame? ReadNextLegacyFrame(StreamReader reader)
    {
        while (reader.ReadLine() is { } legacyLine)
        {
            legacyLine = legacyLine.Trim();
            if (legacyLine.Length == 0)
            {
                continue;
            }

            List<Point> path = ParseLegacyPointLine(legacyLine);
            return new LvfFrame(path.Count == 0 ? new List<List<Point>>() : new List<List<Point>> { path }, new List<RegionShape>(), Color.Black, null);
        }

        return null;
    }

    private static LvfFrame? ReadNextEdgeFrame(StreamReader reader)
    {
        string? line = ReadFrameStart(reader);
        if (line is null)
        {
            return null;
        }

        List<List<Point>> paths = new();
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                return new LvfFrame(paths, new List<RegionShape>(), Color.Black, null);
            }

            if (line.StartsWith("PATH ", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(ParsePathLine(line));
                continue;
            }

            throw new InvalidDataException($"Unexpected line inside frame: {line}");
        }

        throw new InvalidDataException("LVF frame ended before END marker.");
    }

    private static LvfFrame? ReadNextRegionFrame(StreamReader reader)
    {
        string? line = ReadFrameStart(reader);
        if (line is null)
        {
            return null;
        }

        List<RegionShape> regions = new();
        Color background = Color.Black;
        Backdrop? backdrop = null;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                return new LvfFrame(new List<List<Point>>(), regions, background, backdrop);
            }

            if (line.StartsWith("BACKGROUND ", StringComparison.OrdinalIgnoreCase))
            {
                background = ParseBackgroundLine(line);
                continue;
            }

            if (line.StartsWith("BACKDROP ", StringComparison.OrdinalIgnoreCase))
            {
                backdrop = ParseBackdropLine(line);
                continue;
            }

            if (line.StartsWith("REGION ", StringComparison.OrdinalIgnoreCase))
            {
                regions.Add(ParseRegionLine(line));
                continue;
            }

            throw new InvalidDataException($"Unexpected line inside region frame: {line}");
        }

        throw new InvalidDataException("LVFVF2 frame ended before END marker.");
    }

    private static string? ReadFrameStart(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("FRAME ", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }

            throw new InvalidDataException($"Expected FRAME line, got: {line}");
        }

        return null;
    }

    private static string PathToString(List<Point> points)
    {
        StringBuilder builder = new();
        builder.Append("PATH ");
        builder.Append(points.Count.ToString(CultureInfo.InvariantCulture));

        foreach (Point point in points)
        {
            builder.Append(' ');
            builder.Append(point.X.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Y.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string RegionToString(RegionShape region)
    {
        StringBuilder builder = new();
        builder.Append("REGION ");
        builder.Append(region.Id.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(region.Fill.R.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(region.Fill.G.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(region.Fill.B.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(region.Points.Count.ToString(CultureInfo.InvariantCulture));

        foreach (Point point in region.Points)
        {
            builder.Append(' ');
            builder.Append(point.X.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Y.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BackgroundToString(Color background)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"BACKGROUND {background.R} {background.G} {background.B}");
    }

    private static string BackdropToString(Backdrop backdrop)
    {
        StringBuilder builder = new();
        builder.Append("BACKDROP ");
        builder.Append(backdrop.Columns.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(backdrop.Rows.ToString(CultureInfo.InvariantCulture));

        int index = 0;
        while (index < backdrop.Colors.Length)
        {
            Color color = backdrop.Colors[index];
            int run = 1;
            while (index + run < backdrop.Colors.Length && SameRgb(color, backdrop.Colors[index + run]))
            {
                run++;
            }

            builder.Append(' ');
            builder.Append(ColorToHex(color));
            if (run > 1)
            {
                builder.Append('*');
                builder.Append(run.ToString(CultureInfo.InvariantCulture));
            }

            index += run;
        }

        return builder.ToString();
    }

    private static string ColorToHex(Color color)
    {
        return color.R.ToString("X2", CultureInfo.InvariantCulture) +
            color.G.ToString("X2", CultureInfo.InvariantCulture) +
            color.B.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static bool SameRgb(Color a, Color b)
    {
        return a.R == b.R && a.G == b.G && a.B == b.B;
    }

    private static List<Point> ParsePathLine(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int declaredCount))
        {
            throw new InvalidDataException($"Invalid PATH line: {line}");
        }

        List<Point> points = ParsePoints(parts, 2, declaredCount);
        if (points.Count != declaredCount)
        {
            throw new InvalidDataException($"PATH declared {declaredCount} points but contained {points.Count}.");
        }

        return points;
    }

    private static RegionShape ParseRegionLine(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
        {
            throw new InvalidDataException($"Invalid REGION line: {line}");
        }

        int id = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int r = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int g = int.Parse(parts[3], CultureInfo.InvariantCulture);
        int b = int.Parse(parts[4], CultureInfo.InvariantCulture);
        int declaredCount = int.Parse(parts[5], CultureInfo.InvariantCulture);
        List<Point> points = ParsePoints(parts, 6, declaredCount);
        if (points.Count != declaredCount)
        {
            throw new InvalidDataException($"REGION declared {declaredCount} points but contained {points.Count}.");
        }

        return new RegionShape
        {
            Id = id,
            Fill = Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b)),
            Points = points,
            Area = Math.Abs(PolygonArea(points)),
            Center = AveragePoint(points),
            Bounds = BoundsFor(points),
            IsCorrection = id == 0
        };
    }

    private static List<Point> ParsePoints(string[] parts, int start, int declaredCount)
    {
        List<Point> points = new(Math.Max(0, declaredCount));
        for (int i = start; i < parts.Length; i++)
        {
            string[] coords = parts[i].Split(',', StringSplitOptions.TrimEntries);
            if (coords.Length != 2)
            {
                throw new InvalidDataException($"Invalid point token: {parts[i]}");
            }

            int x = int.Parse(coords[0], CultureInfo.InvariantCulture);
            int y = int.Parse(coords[1], CultureInfo.InvariantCulture);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static Color ParseBackgroundLine(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts[0].Equals("BACKGROUND", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid BACKGROUND line: {line}");
        }

        int r = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int g = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int b = int.Parse(parts[3], CultureInfo.InvariantCulture);
        return Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
    }

    private static Backdrop ParseBackdropLine(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("BACKDROP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid BACKDROP line: {line}");
        }

        int columns = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int rows = int.Parse(parts[2], CultureInfo.InvariantCulture);
        if (columns <= 0 || rows <= 0)
        {
            throw new InvalidDataException($"Invalid BACKDROP size: {columns}x{rows}");
        }

        int expected = checked(columns * rows);
        List<Color> colors = new(expected);
        for (int i = 3; i < parts.Length; i++)
        {
            string token = parts[i];
            int run = 1;
            int runSeparator = token.IndexOf('*');
            if (runSeparator >= 0)
            {
                run = int.Parse(token[(runSeparator + 1)..], CultureInfo.InvariantCulture);
                token = token[..runSeparator];
            }

            if (run <= 0)
            {
                throw new InvalidDataException($"Invalid BACKDROP run: {parts[i]}");
            }

            Color color = ParseHexColor(token);
            for (int repeat = 0; repeat < run; repeat++)
            {
                colors.Add(color);
            }
        }

        if (colors.Count != expected)
        {
            throw new InvalidDataException($"BACKDROP declared {expected} colors but contained {colors.Count}.");
        }

        return new Backdrop(columns, rows, colors.ToArray());
    }

    private static Color ParseHexColor(string value)
    {
        if (value.Length != 6)
        {
            throw new InvalidDataException($"Invalid RGB hex color: {value}");
        }

        int r = int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(r, g, b);
    }

    private static List<Point> ParseLegacyPointLine(string line)
    {
        string[] parts = line.Split(new[] { ' ', '\t', 'X', 'Y' }, StringSplitOptions.RemoveEmptyEntries);
        List<Point> points = new(parts.Length / 2);

        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            int x = int.Parse(parts[i], CultureInfo.InvariantCulture);
            int y = int.Parse(parts[i + 1], CultureInfo.InvariantCulture);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static VideoInfo ProbeVideo(string videoPath)
    {
        VideoInfo? ffprobeInfo = ProbeVideoWithFfprobe(videoPath);
        if (ffprobeInfo is not null)
        {
            return ffprobeInfo;
        }

        VideoInfo? captureInfo = ProbeVideoWithOpenCv(videoPath);
        if (captureInfo is not null)
        {
            return captureInfo;
        }

        throw new InvalidOperationException("Could not read video metadata. Install FFmpeg/ffprobe or use a video file OpenCV can inspect.");
    }

    private static VideoInfo? ProbeVideoWithFfprobe(string videoPath)
    {
        try
        {
            ProcessStartInfo startInfo = new("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=width,height,r_frame_rate,avg_frame_rate");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1");
            startInfo.ArgumentList.Add(videoPath);

            using Process probe = Process.Start(startInfo) ?? throw new InvalidOperationException("ffprobe did not start.");
            string output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
            if (probe.ExitCode != 0)
            {
                return null;
            }

            int width = 0;
            int height = 0;
            double fps = 0;
            foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = rawLine.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0] == "width")
                {
                    width = int.Parse(parts[1], CultureInfo.InvariantCulture);
                }
                else if (parts[0] == "height")
                {
                    height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                }
                else if ((parts[0] == "avg_frame_rate" || parts[0] == "r_frame_rate") && fps <= 0)
                {
                    fps = ParseFps(parts[1]);
                }
            }

            return width > 0 && height > 0 && fps > 0 ? new VideoInfo(width, height, fps) : null;
        }
        catch
        {
            return null;
        }
    }

    private static VideoInfo? ProbeVideoWithOpenCv(string videoPath)
    {
        try
        {
            using VideoCapture capture = new(videoPath);
            int width = (int)Math.Round(capture.Get(CapProp.FrameWidth));
            int height = (int)Math.Round(capture.Get(CapProp.FrameHeight));
            double fps = capture.Get(CapProp.Fps);
            return width > 0 && height > 0 && fps > 0 ? new VideoInfo(width, height, fps) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Process StartFfmpegRawVideo(string videoPath, string pixelFormat, AccelerationOptions acceleration)
    {
        ProcessStartInfo startInfo = new("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        if (acceleration.UseHardwareDecode)
        {
            startInfo.ArgumentList.Add("-hwaccel");
            startInfo.ArgumentList.Add("auto");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add(pixelFormat);
        startInfo.ArgumentList.Add("-");

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
        process.ErrorDataReceived += (_, data) =>
        {
            if (!string.IsNullOrWhiteSpace(data.Data))
            {
                Console.Error.WriteLine(data.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static bool ReadFullFrame(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("Video ended in the middle of a raw frame.");
            }

            offset += read;
        }

        return true;
    }

    private static double ParseFps(string value)
    {
        if (value.Contains('/'))
        {
            string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
                denominator != 0)
            {
                return numerator / denominator;
            }
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) ? fps : 0;
    }

    private static string? ReadMeaningfulLine(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return line;
            }
        }

        return null;
    }

    private static string ReadRequired(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? value = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
    }

    private static int ReadInt(string prompt, int fallback, int min, int max)
    {
        Console.Write(prompt);
        string? value = Console.ReadLine();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static AccelerationMode TakeAccelerationOption(List<string> args)
    {
        if (TakeFlag(args, "--cpu", "--no-gpu"))
        {
            return AccelerationMode.Cpu;
        }

        if (TakeFlag(args, "--gpu"))
        {
            return AccelerationMode.Auto;
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--accel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException("Missing value for --accel.");
            }

            string rawValue = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);
            return ParseAccelerationMode(rawValue);
        }

        return AccelerationMode.Auto;
    }

    private static AccelerationMode ParseAccelerationMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => AccelerationMode.Auto,
            "gpu" => AccelerationMode.Auto,
            "cpu" => AccelerationMode.Cpu,
            "none" => AccelerationMode.Cpu,
            "cuda" => AccelerationMode.Cuda,
            "cu" => AccelerationMode.Cuda,
            "opencl" => AccelerationMode.OpenCl,
            "ocl" => AccelerationMode.OpenCl,
            "hybrid" => AccelerationMode.Hybrid,
            "cuda-opencl" => AccelerationMode.Hybrid,
            "opencl-cuda" => AccelerationMode.Hybrid,
            "ffmpeg" => AccelerationMode.FfmpegHardwareDecode,
            "hwdecode" => AccelerationMode.FfmpegHardwareDecode,
            _ => throw new ArgumentException($"Unknown acceleration mode: {value}")
        };
    }

    private static AccelerationOptions CreateAccelerationOptions(AccelerationMode mode)
    {
        CudaLabeler? cudaLabeler = null;
        if (mode is AccelerationMode.Auto or AccelerationMode.Cuda or AccelerationMode.Hybrid)
        {
            cudaLabeler = CudaLabeler.TryCreate();
        }

        if ((mode is AccelerationMode.Cuda or AccelerationMode.Hybrid) && cudaLabeler is null)
        {
            throw new InvalidOperationException("CUDA acceleration was requested, but no CUDA accelerator was available through ILGPU.");
        }

        bool openClAvailable = SafeHaveOpenCl() && SafeHaveOpenClGpu();
        if ((mode is AccelerationMode.OpenCl or AccelerationMode.Hybrid) && !openClAvailable)
        {
            cudaLabeler?.Dispose();
            throw new InvalidOperationException("OpenCL acceleration was requested, but no compatible OpenCL GPU device was found.");
        }

        bool useOpenCl = mode is AccelerationMode.OpenCl or AccelerationMode.Hybrid || (mode == AccelerationMode.Auto && cudaLabeler is null && openClAvailable);
        bool useHardwareDecode = mode is AccelerationMode.Auto or AccelerationMode.FfmpegHardwareDecode or AccelerationMode.Cuda or AccelerationMode.Hybrid;
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);

        return new AccelerationOptions(mode, useOpenCl, useHardwareDecode, workerCount, cudaLabeler);
    }

    private static void ConfigureAcceleration(AccelerationOptions acceleration)
    {
        try
        {
            CvInvoke.UseOpenCL = acceleration.UseOpenCl;
        }
        catch
        {
            CvInvoke.UseOpenCL = false;
        }
    }

    private static string DescribeAcceleration(AccelerationOptions acceleration)
    {
        string openCl = acceleration.UseOpenCl ? "OpenCL preprocess on" : "OpenCL preprocess off";
        string cuda = acceleration.CudaLabeler is null ? "CUDA labels off" : $"CUDA labels on ({acceleration.CudaLabeler.DeviceName})";
        string decode = acceleration.UseHardwareDecode ? "FFmpeg hwdecode auto" : "software decode";
        return $"{acceleration.ModeName}, {cuda}, {openCl}, {decode}, {acceleration.WorkerCount} CPU workers";
    }

    private static bool SafeHaveOpenCl()
    {
        try
        {
            return CvInvoke.HaveOpenCL;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeHaveOpenClGpu()
    {
        try
        {
            return CvInvoke.HaveOpenCLCompatibleGpuDevice;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeUseOpenCl()
    {
        try
        {
            return CvInvoke.UseOpenCL;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeOpenClSummary()
    {
        try
        {
            return CvInvoke.OclGetPlatformsSummary();
        }
        catch
        {
            return "";
        }
    }

    private static bool TakeFlag(List<string> args, params string[] names)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private static int TakeIntOption(List<string> args, int fallback, int min, int max, params string[] names)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }

            string rawValue = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);

            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? Math.Clamp(parsed, min, max)
                : throw new ArgumentException($"Invalid numeric value: {rawValue}");
        }

        return fallback;
    }

    private static double TakeDoubleOption(List<string> args, double fallback, params string[] names)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }

            string rawValue = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);

            return double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : throw new ArgumentException($"Invalid numeric value: {rawValue}");
        }

        return fallback;
    }

    private static string? TakeStringOption(List<string> args, params string[] names)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }

            string rawValue = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);
            return rawValue;
        }

        return null;
    }

    private static CompressionLevel TakeCompressionOption(List<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--compression", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException("Missing value for --compression.");
            }

            string rawValue = args[i + 1];
            args.RemoveAt(i + 1);
            args.RemoveAt(i);
            return ParseCompressionLevel(rawValue);
        }

        if (TakeFlag(args, "--fast-compression", "--fast-compress"))
        {
            return CompressionLevel.Fastest;
        }

        return CompressionLevel.Optimal;
    }

    private static RegionTracerMode TakeTracerOption(List<string> args)
    {
        string? rawValue = TakeStringOption(args, "--tracer", "--trace-engine");
        return rawValue is null ? RegionTracerMode.OpenCv : ParseTracerMode(rawValue);
    }

    private static RegionTracerMode ParseTracerMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "custom" => RegionTracerMode.Custom,
            "native" => RegionTracerMode.Custom,
            "lvfvf" => RegionTracerMode.Custom,
            "merged" => RegionTracerMode.Merged,
            "merge" => RegionTracerMode.Merged,
            "neighbor" => RegionTracerMode.Merged,
            "neighbors" => RegionTracerMode.Merged,
            "merged-fast" => RegionTracerMode.MergedFast,
            "merge-fast" => RegionTracerMode.MergedFast,
            "fast-merged" => RegionTracerMode.MergedFast,
            "fastmerge" => RegionTracerMode.MergedFast,
            "mean-shift" => RegionTracerMode.MergedFast,
            "meanshift" => RegionTracerMode.MergedFast,
            "region" => RegionTracerMode.Merged,
            "regions" => RegionTracerMode.Merged,
            "opencv" => RegionTracerMode.OpenCv,
            "cv" => RegionTracerMode.OpenCv,
            _ => throw new ArgumentException($"Unknown tracer mode: {value}")
        };
    }

    private static CompressionLevel ParseCompressionLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "fast" => CompressionLevel.Fastest,
            "fastest" => CompressionLevel.Fastest,
            "speed" => CompressionLevel.Fastest,
            "optimal" => CompressionLevel.Optimal,
            "balanced" => CompressionLevel.Optimal,
            "small" => CompressionLevel.SmallestSize,
            "smallest" => CompressionLevel.SmallestSize,
            "smallestsize" => CompressionLevel.SmallestSize,
            "none" => CompressionLevel.NoCompression,
            "off" => CompressionLevel.NoCompression,
            _ => throw new ArgumentException($"Unknown compression mode: {value}")
        };
    }

    private static string DescribeCompression(CompressionLevel compressionLevel)
    {
        return compressionLevel switch
        {
            CompressionLevel.Fastest => "fast",
            CompressionLevel.Optimal => "optimal",
            CompressionLevel.SmallestSize => "smallest",
            CompressionLevel.NoCompression => "none",
            _ => compressionLevel.ToString()
        };
    }

    private static string DescribeTracer(RegionTracerMode tracerMode)
    {
        return tracerMode switch
        {
            RegionTracerMode.Custom => "custom label-boundary tracer",
            RegionTracerMode.MergedFast => "fast blur/snap neighbor merge tracer",
            RegionTracerMode.Merged => "merged neighbor-region tracer",
            RegionTracerMode.OpenCv => "OpenCV FindContours",
            _ => tracerMode.ToString()
        };
    }

    private static int PaletteSizeForQuality(int quality)
    {
        return Math.Clamp((int)Math.Round(8 + quality * 0.34), 8, 48);
    }

    private static double MinRegionArea(int width, int height, int quality)
    {
        double ratio = 0.00005 + (100 - quality) * 0.000035;
        return Math.Max(4, width * height * ratio);
    }

    private static double MergedRegionArea(int width, int height, int quality)
    {
        return Math.Max(36, MinRegionArea(width, height, quality) * 0.08);
    }

    private static double SimplifyForQuality(int quality)
    {
        return Math.Max(0.65, (101 - quality) / 15.0);
    }

    private static int InitialNeighborMergeThreshold(int quality)
    {
        return Math.Clamp((int)Math.Round(4 + (100 - quality) * 0.08), 4, 9);
    }

    private static int BlurredNeighborBoundaryThreshold(int quality)
    {
        return Math.Clamp((int)Math.Round(13 + (100 - quality) * 0.14), 12, 22);
    }

    private static int BlurredNeighborRegionThreshold(int quality)
    {
        return Math.Clamp((int)Math.Round(8 + (100 - quality) * 0.1), 8, 16);
    }

    private static int FastMergedColorStep(int quality)
    {
        return Math.Clamp((int)Math.Round(6 + (100 - quality) * 0.08), 6, 12);
    }

    private static int PaletteFitMergeThreshold(int quality, int paletteSize)
    {
        return Math.Clamp((int)Math.Round(20 + (100 - quality) * 0.18 + Math.Max(0, paletteSize - 36) * 0.04), 20, 34);
    }

    private static int PaletteGuidedRegionMergeThreshold(int quality, int paletteSize)
    {
        return Math.Clamp((int)Math.Round(15 + (100 - quality) * 0.16 + Math.Max(0, paletteSize - 36) * 0.035), 15, 26);
    }

    private static int PaletteGuidedLargeRegionMergeThreshold(int quality)
    {
        return Math.Clamp((int)Math.Round(7 + (100 - quality) * 0.08), 7, 12);
    }

    private static int PaletteGuidedBoundaryMergeThreshold(int quality, int paletteSize)
    {
        return Math.Clamp((int)Math.Round(14 + (100 - quality) * 0.13 + Math.Max(0, paletteSize - 36) * 0.03), 14, 24);
    }

    private static int PaletteGuidedAbsorbLimit(int width, int height, int quality)
    {
        return Math.Clamp((int)Math.Round(MinRegionArea(width, height, quality) * 0.55), 128, 1200);
    }

    private static int ErrorThresholdForQuality(int quality)
    {
        return ErrorThresholdForQuality(quality, DefaultCorrectionStrength);
    }

    private static int ErrorThresholdForQuality(int quality, int correctionStrength)
    {
        int baseThreshold = Math.Clamp((int)Math.Round(72 - quality * 0.42), 24, 58);
        int relaxed = (int)Math.Round((100 - correctionStrength) * 0.28);
        return Math.Clamp(baseThreshold + relaxed, baseThreshold, 76);
    }

    private static double ErrorRegionArea(int width, int height, int quality)
    {
        return ErrorRegionArea(width, height, quality, DefaultCorrectionStrength);
    }

    private static double ErrorRegionArea(int width, int height, int quality, int correctionStrength)
    {
        double strengthScale = Math.Clamp(1.0 + (100 - correctionStrength) * 0.025, 1.0, 3.5);
        return Math.Max(3, MinRegionArea(width, height, quality) * 0.45 * strengthScale);
    }

    private static double ErrorSimplifyForQuality(int quality)
    {
        return Math.Max(0.45, SimplifyForQuality(quality) * 0.75);
    }

    private static int PatchDetailMinBounds(int quality, int patchDetail)
    {
        return Math.Clamp((int)Math.Round(48 - quality * 0.18 - patchDetail * 0.15), 16, 42);
    }

    private static double PatchDetailErrorThreshold(int quality, int patchDetail)
    {
        return Math.Clamp(42 - quality * 0.16 - patchDetail * 0.18, 12, 34);
    }

    private static double PatchDetailOwnerThreshold(int quality, int patchDetail)
    {
        return Math.Clamp(8 + (100 - quality) * 0.04 + patchDetail * 0.03, 8, 14);
    }

    private static double PatchDetailSourceRegionArea(int width, int height, int quality, int patchDetail)
    {
        double qualityArea = MinRegionArea(width, height, quality) * Math.Clamp(12 - patchDetail * 0.06, 5, 12);
        double frameArea = width * height * Math.Clamp(0.003 - patchDetail * 0.000015, 0.0012, 0.003);
        return Math.Max(qualityArea, frameArea);
    }

    private static int PatchDetailMinimumBinPixels(int width, int height, int quality, int patchDetail)
    {
        double factor = Math.Clamp(0.12 - patchDetail * 0.0007, 0.045, 0.12);
        return Math.Max(14, (int)Math.Round(ErrorRegionArea(width, height, quality) * factor));
    }

    private static double PatchDetailRegionArea(int width, int height, int quality, int patchDetail)
    {
        double factor = Math.Clamp(0.16 - patchDetail * 0.0008, 0.06, 0.16);
        return Math.Max(18, ErrorRegionArea(width, height, quality) * factor);
    }

    private static double PatchDetailSimplifyForQuality(int quality, int patchDetail)
    {
        return Math.Clamp(SimplifyForQuality(quality) * 0.6 - patchDetail * 0.003, 0.35, 1.1);
    }

    private static int ForegroundDifferenceThreshold(int objectFocus)
    {
        return Math.Clamp((int)Math.Round(44 - objectFocus * 0.22), 18, 44);
    }

    private static double PatchDetailSourceRegionScore(RegionShape region, byte[]? foregroundMask, int width, int height, int objectFocus)
    {
        if (foregroundMask is null || objectFocus <= 0)
        {
            return region.Area;
        }

        Rectangle bounds = Rectangle.Intersect(region.Bounds, new Rectangle(0, 0, width, height));
        if (bounds.IsEmpty)
        {
            return region.Area;
        }

        int step = Math.Max(4, Math.Min(bounds.Width, bounds.Height) / 12);
        int foreground = 0;
        int samples = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y += step)
        {
            int row = y * width;
            for (int x = bounds.Left; x < bounds.Right; x += step)
            {
                samples++;
                if (foregroundMask[row + x] != 0)
                {
                    foreground++;
                }
            }
        }

        double coverage = samples == 0 ? 0 : foreground / (double)samples;
        return region.Area * (1.0 + coverage * objectFocus / 18.0);
    }

    private static int MaxPatchDetailSourceRegions(int patchDetail)
    {
        return Math.Clamp(8 + patchDetail / 3, 8, 36);
    }

    private static int MaxPatchDetailColorBinsForRegion(int patchDetail)
    {
        return Math.Clamp(2 + patchDetail / 12, 3, 10);
    }

    private static int MaxPatchDetailRegionsForQuality(int quality, int patchDetail)
    {
        if (patchDetail <= 0)
        {
            return 0;
        }

        return Math.Clamp(24 + quality / 2 + patchDetail, 48, 220);
    }

    private static int MaxErrorRegionsForQuality(int quality)
    {
        return MaxErrorRegionsForQuality(quality, DefaultCorrectionStrength);
    }

    private static int MaxErrorRegionsForQuality(int quality, int correctionStrength)
    {
        int baseCount = Math.Clamp(quality * 3 - 150, 36, 180);
        return Math.Max(0, (int)Math.Round(baseCount * (correctionStrength / 100.0)));
    }

    private static int MaxErrorColorBinsForQuality(int quality)
    {
        return MaxErrorColorBinsForQuality(quality, DefaultCorrectionStrength);
    }

    private static int MaxErrorColorBinsForQuality(int quality, int correctionStrength)
    {
        int baseCount = Math.Clamp((quality - 34) / 4, 6, 18);
        return Math.Max(1, (int)Math.Round(baseCount * Math.Clamp(correctionStrength / 100.0, 0.15, 1.0)));
    }

    private static double Luminance(Color color)
    {
        return color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
    }

    private static PointF AveragePoint(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return new PointF();
        }

        double x = 0;
        double y = 0;
        foreach (Point point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new PointF((float)(x / points.Count), (float)(y / points.Count));
    }

    private static Rectangle BoundsFor(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return Rectangle.Empty;
        }

        int minX = points[0].X;
        int maxX = points[0].X;
        int minY = points[0].Y;
        int maxY = points[0].Y;

        foreach (Point point in points)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static double PolygonArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point a = points[i];
            Point b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    private static double Distance(PointF a, PointF b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceToSegmentSquared(Point point, Point start, Point end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        if (dx == 0 && dy == 0)
        {
            return DistanceSquared(point, start);
        }

        double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        double closestX = start.X + t * dx;
        double closestY = start.Y + t * dy;
        double px = point.X - closestX;
        double py = point.Y - closestY;
        return px * px + py * py;
    }

    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static double IntersectionOverUnion(Rectangle a, Rectangle b)
    {
        Rectangle intersection = Rectangle.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return 0;
        }

        double intersectionArea = intersection.Width * intersection.Height;
        double unionArea = a.Width * a.Height + b.Width * b.Height - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    private static int ClampByte(int value)
    {
        return Math.Clamp(value, 0, 255);
    }

    private static string DescribeFormat(LvfHeader header)
    {
        return header.Format switch
        {
            LvfFormat.RegionV2 => "LVFVF2 traced regions",
            LvfFormat.EdgeV1 => "LVFVF1 edge paths",
            LvfFormat.Legacy => "legacy point stream",
            _ => "unknown"
        };
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private enum LvfFormat
    {
        Legacy,
        EdgeV1,
        RegionV2
    }

    private enum AccelerationMode
    {
        Auto,
        Cpu,
        Cuda,
        OpenCl,
        Hybrid,
        FfmpegHardwareDecode
    }

    private enum RegionTracerMode
    {
        Custom,
        MergedFast,
        Merged,
        OpenCv
    }

    private enum EncodeStage
    {
        DecodeRead,
        FrameCopy,
        Preprocess,
        BuildPalette,
        AssignLabels,
        BuildMasks,
        MergePrepass,
        TraceContours,
        ObjectMask,
        TraceDetails,
        TraceResiduals,
        TrackIds,
        Write
    }

    private sealed class EncodeProfiler
    {
        private readonly long[] _ticks = new long[Enum.GetValues<EncodeStage>().Length];

        public void Add(EncodeStage stage, long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Add(ref _ticks[(int)stage], elapsed);
        }

        public void Print(int frameCount)
        {
            double totalSeconds = _ticks.Sum() / (double)Stopwatch.Frequency;
            Console.WriteLine();
            Console.WriteLine("Profile:");
            Console.WriteLine($"  Measured stage time: {FormatDouble(totalSeconds)}s across {frameCount} frames");

            foreach (EncodeStage stage in Enum.GetValues<EncodeStage>())
            {
                double seconds = _ticks[(int)stage] / (double)Stopwatch.Frequency;
                double perFrameMs = frameCount <= 0 ? 0 : seconds * 1000 / frameCount;
                double percent = totalSeconds <= 0 ? 0 : seconds / totalSeconds * 100;
                Console.WriteLine($"  {StageName(stage),-16} {FormatDouble(seconds),7}s  {FormatDouble(perFrameMs),7} ms/frame  {percent,5:0.0}%");
            }
        }

        private static string StageName(EncodeStage stage)
        {
            return stage switch
            {
                EncodeStage.DecodeRead => "decode/read",
                EncodeStage.FrameCopy => "frame copy",
                EncodeStage.Preprocess => "preprocess",
                EncodeStage.BuildPalette => "palette",
                EncodeStage.AssignLabels => "labels",
                EncodeStage.BuildMasks => "masks",
                EncodeStage.MergePrepass => "merge prepass",
                EncodeStage.TraceContours => "contours",
                EncodeStage.ObjectMask => "object mask",
                EncodeStage.TraceDetails => "details",
                EncodeStage.TraceResiduals => "residuals",
                EncodeStage.TrackIds => "track IDs",
                EncodeStage.Write => "write",
                _ => stage.ToString()
            };
        }
    }

    private abstract class RegionFrameWriter : IDisposable
    {
        public abstract void WriteHeader(VideoInfo info, int quality, int paletteSize);
        public abstract void WriteFrame(int frameNumber, TracedRegions traced, List<RegionShape> regions);
        public abstract void Dispose();
    }

    private sealed class TextRegionFrameWriter : RegionFrameWriter
    {
        private readonly StreamWriter _writer;

        public TextRegionFrameWriter(Stream stream)
        {
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
        }

        public override void WriteHeader(VideoInfo info, int quality, int paletteSize)
        {
            WriteRegionHeader(_writer, info, quality, paletteSize);
        }

        public override void WriteFrame(int frameNumber, TracedRegions traced, List<RegionShape> regions)
        {
            _writer.WriteLine($"FRAME {frameNumber}");
            _writer.WriteLine(BackgroundToString(traced.Background));
            foreach (RegionShape region in regions)
            {
                _writer.WriteLine(RegionToString(region));
            }

            _writer.WriteLine("END");
        }

        public override void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed class BinaryRegionFrameWriter : RegionFrameWriter
    {
        private readonly BinaryWriter _writer;

        public BinaryRegionFrameWriter(Stream stream)
        {
            _writer = new BinaryWriter(stream, Encoding.UTF8);
        }

        public override void WriteHeader(VideoInfo info, int quality, int paletteSize)
        {
            _writer.Write(BinaryRegionMagic);
            _writer.Write(BinaryRegionVersion);
            WriteVarInt(_writer, info.Width);
            WriteVarInt(_writer, info.Height);
            _writer.Write(info.Fps);
            WriteVarInt(_writer, quality);
            WriteVarInt(_writer, paletteSize);
        }

        public override void WriteFrame(int frameNumber, TracedRegions traced, List<RegionShape> regions)
        {
            _writer.Write(BinaryFrameMarker);
            WriteVarInt(_writer, frameNumber);
            WriteBinaryColor(_writer, traced.Background);

            WriteVarInt(_writer, regions.Count);
            foreach (RegionShape region in regions)
            {
                WriteVarInt(_writer, region.Id);
                WriteBinaryColor(_writer, region.Fill);
                WriteVarInt(_writer, region.Points.Count);
                foreach (Point point in region.Points)
                {
                    WriteVarInt(_writer, point.X);
                    WriteVarInt(_writer, point.Y);
                }
            }
        }

        public override void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed record VideoInfo(int Width, int Height, double Fps);
    private sealed record LvfHeader(double Fps, int Width, int Height, LvfFormat Format, int Quality, int PaletteSize, int BinaryVersion = 0);
    private sealed record LvfFrame(List<List<Point>> Paths, List<RegionShape> Regions, Color Background, Backdrop? Backdrop);
    private sealed record TracedRegions(Color Background, List<RegionShape> Regions);
    private sealed record EncodedFrame(int FrameNumber, TracedRegions Traced, Palette Palette);
    private sealed record WriteFrameResult(long Regions, long Corrections, long Points, List<RegionSignature> PreviousRegions);
    private sealed record Backdrop(int Columns, int Rows, Color[] Colors);
    private readonly record struct Edge(int X1, int Y1, int X2, int Y2);
    private sealed record GpuFrameData(Color Background, float[] Vertices)
    {
        public int VertexCount => Vertices.Length / 5;
    }

    private sealed class GpuPlaybackWindow : GameWindow
    {
        private const int FloatsPerVertex = 5;
        private readonly LvfHeader _header;
        private readonly BlockingCollection<GpuFrameData> _frames;
        private readonly double _frameMs;
        private readonly Stopwatch _clock = new();
        private int _program;
        private int _vertexArray;
        private int _vertexBuffer;
        private GpuFrameData? _currentFrame;

        public GpuPlaybackWindow(LvfHeader header, BlockingCollection<GpuFrameData> frames, string fileName)
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
            _program = CreateShaderProgram();
            _vertexArray = GL.GenVertexArray();
            _vertexBuffer = GL.GenBuffer();

            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 2 * sizeof(float));
            GL.BindVertexArray(0);

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            UpdateViewport();
            _clock.Start();
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            UpdateViewport();
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

        private static NativeWindowSettings CreateNativeWindowSettings(LvfHeader header, string fileName)
        {
            Vector2i size = InitialWindowSize(header.Width, header.Height);
            return new NativeWindowSettings
            {
                ClientSize = size,
                Title = $"LVFVF GPU Player - {fileName}"
            };
        }

        private static Vector2i InitialWindowSize(int width, int height)
        {
            int maxWidth = 1280;
            int maxHeight = 720;
            double scale = Math.Min(1.0, Math.Min(maxWidth / (double)Math.Max(1, width), maxHeight / (double)Math.Max(1, height)));
            return new Vector2i(Math.Max(320, (int)Math.Round(width * scale)), Math.Max(180, (int)Math.Round(height * scale)));
        }

        private void TryAdvanceFrame()
        {
            double nextFrameTime = DisplayedFrames * _frameMs;
            if (_currentFrame is not null && _clock.Elapsed.TotalMilliseconds < nextFrameTime)
            {
                return;
            }

            if (_frames.TryTake(out GpuFrameData? frame, _currentFrame is null ? 100 : 0))
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
            Color background = _currentFrame?.Background ?? Color.Black;
            GL.ClearColor(background.R / 255f, background.G / 255f, background.B / 255f, 1f);
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

        private void UploadFrame(GpuFrameData frame)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, frame.Vertices.Length * sizeof(float), frame.Vertices, BufferUsageHint.StreamDraw);
        }

        private void UpdateViewport()
        {
            int windowWidth = Math.Max(1, ClientSize.X);
            int windowHeight = Math.Max(1, ClientSize.Y);
            double videoAspect = _header.Width / (double)Math.Max(1, _header.Height);
            double windowAspect = windowWidth / (double)windowHeight;

            int viewportWidth;
            int viewportHeight;
            if (windowAspect > videoAspect)
            {
                viewportHeight = windowHeight;
                viewportWidth = Math.Max(1, (int)Math.Round(viewportHeight * videoAspect));
            }
            else
            {
                viewportWidth = windowWidth;
                viewportHeight = Math.Max(1, (int)Math.Round(viewportWidth / videoAspect));
            }

            int x = (windowWidth - viewportWidth) / 2;
            int y = (windowHeight - viewportHeight) / 2;
            GL.Viewport(x, y, viewportWidth, viewportHeight);
        }

        private static int CreateShaderProgram()
        {
            int vertexShader = CompileShader(ShaderType.VertexShader, """
                #version 330 core
                layout (location = 0) in vec2 aPosition;
                layout (location = 1) in vec3 aColor;
                out vec3 vColor;

                void main()
                {
                    gl_Position = vec4(aPosition, 0.0, 1.0);
                    vColor = aColor;
                }
                """);

            int fragmentShader = CompileShader(ShaderType.FragmentShader, """
                #version 330 core
                in vec3 vColor;
                out vec4 FragColor;

                void main()
                {
                    FragColor = vec4(vColor, 1.0);
                }
                """);

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
            if (linkStatus == 0)
            {
                string log = GL.GetProgramInfoLog(program);
                GL.DeleteProgram(program);
                GL.DeleteShader(vertexShader);
                GL.DeleteShader(fragmentShader);
                throw new InvalidOperationException($"GPU shader link failed: {log}");
            }

            GL.DetachShader(program, vertexShader);
            GL.DetachShader(program, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
            return program;
        }

        private static int CompileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compileStatus);
            if (compileStatus == 0)
            {
                string log = GL.GetShaderInfoLog(shader);
                GL.DeleteShader(shader);
                throw new InvalidOperationException($"GPU shader compile failed: {log}");
            }

            return shader;
        }
    }

    private sealed record AccelerationOptions(AccelerationMode Mode, bool UseOpenCl, bool UseHardwareDecode, int WorkerCount, CudaLabeler? CudaLabeler) : IDisposable
    {
        public string ModeName => Mode switch
        {
            AccelerationMode.Auto => "auto",
            AccelerationMode.Cpu => "cpu",
            AccelerationMode.Cuda => "cuda",
            AccelerationMode.OpenCl => "opencl",
            AccelerationMode.Hybrid => "hybrid",
            AccelerationMode.FfmpegHardwareDecode => "ffmpeg",
            _ => "unknown"
        };

        public void Dispose()
        {
            CudaLabeler?.Dispose();
        }
    }

    private sealed record Palette(double[] B, double[] G, double[] R)
    {
        public int Size => B.Length;
    }

    private readonly record struct MergeColorSample(double B, double G, double R, double Weight);

    private sealed record RegionSignature(int Id, Color Fill, PointF Center, double Area, Rectangle Bounds)
    {
        public static RegionSignature FromShape(RegionShape shape)
        {
            return new RegionSignature(shape.Id, shape.Fill, shape.Center, shape.Area, shape.Bounds);
        }
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public DisjointSet(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (int i = 0; i < count; i++)
            {
                _parent[i] = i;
            }
        }

        public int Find(int value)
        {
            int root = value;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            while (_parent[value] != value)
            {
                int next = _parent[value];
                _parent[value] = root;
                value = next;
            }

            return root;
        }

        public void Union(int a, int b)
        {
            Union(a, b, out _, out _);
        }

        public bool Union(int a, int b, out int mergedRoot, out int absorbedRoot)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA == rootB)
            {
                mergedRoot = rootA;
                absorbedRoot = rootB;
                return false;
            }

            if (_rank[rootA] < _rank[rootB])
            {
                _parent[rootA] = rootB;
                mergedRoot = rootB;
                absorbedRoot = rootA;
                return true;
            }

            if (_rank[rootA] > _rank[rootB])
            {
                _parent[rootB] = rootA;
                mergedRoot = rootA;
                absorbedRoot = rootB;
                return true;
            }

            _parent[rootB] = rootA;
            _rank[rootA]++;
            mergedRoot = rootA;
            absorbedRoot = rootB;
            return true;
        }
    }

    private sealed class CudaLabeler : IDisposable
    {
        private readonly Context _context;
        private readonly Accelerator _accelerator;
        private readonly object _labelLock = new();
        private readonly Action<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, int> _assignLabelsKernel;

        private CudaLabeler(Context context, Accelerator accelerator)
        {
            _context = context;
            _accelerator = accelerator;
            _assignLabelsKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<byte>,
                ArrayView<float>,
                ArrayView<float>,
                ArrayView<float>,
                ArrayView<byte>,
                int>(AssignPaletteLabelsCudaKernel);
        }

        public string DeviceName => _accelerator.Name;

        public static bool IsAvailable()
        {
            using CudaLabeler? labeler = TryCreate();
            return labeler is not null;
        }

        public static string GetSummary()
        {
            try
            {
                using Context context = Context.Create(builder => builder.Default());
                StringBuilder builder = new();
                foreach (Device device in context.Devices.Where(device => device.AcceleratorType == AcceleratorType.Cuda))
                {
                    builder.AppendLine($"CUDA device: {device.Name}");
                }

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return $"CUDA device query failed: {ex.Message}";
            }
        }

        public static CudaLabeler? TryCreate()
        {
            try
            {
                Context context = Context.Create(builder => builder.Default());
                Device? device = context.Devices.FirstOrDefault(device => device.AcceleratorType == AcceleratorType.Cuda);
                if (device is null)
                {
                    context.Dispose();
                    return null;
                }

                Accelerator accelerator = device.CreateAccelerator(context);
                return new CudaLabeler(context, accelerator);
            }
            catch
            {
                return null;
            }
        }

        public byte[] AssignPaletteLabels(byte[] pixels, Palette palette)
        {
            lock (_labelLock)
            {
                int pixelCount = pixels.Length / 3;
                byte[] labels = new byte[pixelCount];
                float[] centerB = palette.B.Select(value => (float)value).ToArray();
                float[] centerG = palette.G.Select(value => (float)value).ToArray();
                float[] centerR = palette.R.Select(value => (float)value).ToArray();

                using MemoryBuffer1D<byte, Stride1D.Dense> pixelBuffer = _accelerator.Allocate1D<byte>(pixels.Length);
                using MemoryBuffer1D<float, Stride1D.Dense> centerBBuffer = _accelerator.Allocate1D<float>(centerB.Length);
                using MemoryBuffer1D<float, Stride1D.Dense> centerGBuffer = _accelerator.Allocate1D<float>(centerG.Length);
                using MemoryBuffer1D<float, Stride1D.Dense> centerRBuffer = _accelerator.Allocate1D<float>(centerR.Length);
                using MemoryBuffer1D<byte, Stride1D.Dense> labelBuffer = _accelerator.Allocate1D<byte>(pixelCount);

                pixelBuffer.CopyFromCPU(pixels);
                centerBBuffer.CopyFromCPU(centerB);
                centerGBuffer.CopyFromCPU(centerG);
                centerRBuffer.CopyFromCPU(centerR);

                _assignLabelsKernel(pixelCount, pixelBuffer.View, centerBBuffer.View, centerGBuffer.View, centerRBuffer.View, labelBuffer.View, palette.Size);
                _accelerator.Synchronize();
                labelBuffer.CopyToCPU(labels);

                return labels;
            }
        }

        public void Dispose()
        {
            _accelerator.Dispose();
            _context.Dispose();
        }
    }

    private sealed class RegionShape
    {
        public int Id { get; set; }
        public Color Fill { get; set; }
        public List<Point> Points { get; set; } = new();
        public double Area { get; set; }
        public PointF Center { get; set; }
        public Rectangle Bounds { get; set; }
        public bool IsCorrection { get; set; }
    }

    private static void AssignPaletteLabelsCudaKernel(
        Index1D index,
        ArrayView<byte> pixels,
        ArrayView<float> centerB,
        ArrayView<float> centerG,
        ArrayView<float> centerR,
        ArrayView<byte> labels,
        int paletteSize)
    {
        int pixel = index;
        int offset = pixel * 3;
        float b = pixels[offset];
        float g = pixels[offset + 1];
        float r = pixels[offset + 2];

        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < paletteSize; i++)
        {
            float db = b - centerB[i];
            float dg = g - centerG[i];
            float dr = r - centerR[i];
            float distance = db * db + dg * dg + dr * dr;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        labels[pixel] = (byte)best;
    }
}
