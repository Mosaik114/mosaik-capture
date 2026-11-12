using System.Runtime.InteropServices;

namespace MosaikCapture.Engine.Tests.Recording;

// Every ffmpeg/ffprobe call is built from single arguments; the command line must split back into
// exactly those arguments the way Windows programs (CommandLineToArgvW / the C runtime) do it.
public sealed class FfmpegArgsTests
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Splits like the receiving program would (the first token is the program name).</summary>
    static string[] Split(string arguments)
    {
        IntPtr argv = CommandLineToArgvW("ffmpeg.exe " + arguments, out int count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("C:\\Videos\\out.mp4", "C:\\Videos\\out.mp4")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\with space\\", "\"C:\\with space\\\\\"")]
    [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData("scale='min(960,iw)':-2", "scale='min(960,iw)':-2")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void QuotesOnlyWhenNeeded(string argument, string expected) => Assert.Equal(expected, FfmpegArgs.Quote(argument));

    [Theory]
    [InlineData("C:\\Users\\Jane Doe\\Videos\\Mosaiks-Aufnahme 2026-09-26 14-03-12.mp4")]
    [InlineData("C:\\Temp\\it's here\\")]
    [InlineData("\\\\server\\share\\a \"quoted\" name.mp4")]
    [InlineData("trailing backslashes\\\\\\")]
    [InlineData("")]
    [InlineData("Grüße ß € \U0001F3AC")]
    [InlineData("[0:v]scale=-2:60,setsar=1[v0];[v0]null")]
    public void EveryArgumentSurvivesTheCommandLine(string tricky)
    {
        var args = new FfmpegArgs().Add("-i", tricky, "-map", "0:v", tricky);

        Assert.Equal(new[] { "-i", tricky, "-map", "0:v", tricky }, Split(args.ToString()));
    }

    [Fact]
    public void RandomArgumentsSurviveTheCommandLine()
    {
        const string Alphabet = "ab \\\"\t'C:;[]";
        var random = new Random(3);
        for (int round = 0; round < 500; round++)
        {
            var values = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new string(Enumerable.Range(0, random.Next(0, 12)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray()))
                .ToArray();

            Assert.Equal(values, Split(new FfmpegArgs().Add(values).ToString()));
        }
    }

    [Fact]
    public void QuietStartsEveryCall() =>
        Assert.Equal(new[] { "-hide_banner", "-loglevel", "error", "-y" }, FfmpegArgs.Quiet().Arguments);

    [Fact]
    public void BatchCallsNeverReadStdin() =>
        Assert.Equal(new[] { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" }, FfmpegArgs.Batch().Arguments);

    [Fact]
    public void OnlyRecordingSegmentsKeepStdinForQuit()
    {
        Assert.DoesNotContain("-nostdin", RecordingSession.SegmentArguments(0, 30, new Int32Rect(0, 0, 100, 100), 0, 0, 0, VideoEncoder.Software, "C:\\a.mkv").Arguments);
        Assert.Contains("-nostdin", RecordingSession.Assemble("C:\\l.txt", new List<string>(), new List<double>(), new List<double>(), "C:\\o.mp4").Arguments);
        Assert.Contains("-nostdin", VideoExport.Mp4Arguments("C:\\i.mp4", 0, 1, false, VideoEncoder.Software, "C:\\o.mp4").Arguments);
        Assert.Contains("-nostdin", VideoExport.GifPaletteArguments("C:\\i.mp4", 0, 1, "C:\\p.png").Arguments);
        Assert.Contains("-nostdin", VideoExport.GifArguments("C:\\i.mp4", 0, 1, "C:\\p.png", "C:\\o.gif").Arguments);
        Assert.Contains("-nostdin", RecordedVideo.FrameArguments("C:\\v.mp4", 1, 640, "C:\\f.png").Arguments);
        Assert.Contains("-nostdin", RecordedVideo.StripArguments("C:\\v.mp4", 6, 3, 60, "C:\\s.png").Arguments);
    }

    [Fact]
    public void AddKeepsTheOrder()
    {
        var args = new FfmpegArgs().Add("-a").Add(new List<string> { "-b", "c" }).Add("-d", "e");

        Assert.Equal(new[] { "-a", "-b", "c", "-d", "e" }, args.Arguments);
        Assert.Equal("-a -b c -d e", args.ToString());
    }
}

// The command lines the engine hands to ffmpeg, argument by argument.
public sealed class CommandLineTests
{
    [Fact]
    public void SegmentWithNvencStaysOnTheGpu()
    {
        var args = RecordingSession.SegmentArguments(1, 60, new Int32Rect(100, 50, 640, 360), 100, 50, 1_700_000_000_000_000, VideoEncoder.Nvenc, "C:\\Rec Dir\\segment-01.mkv");

        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "ddagrab=output_idx=1:framerate=60:video_size=640x360:offset_x=100:offset_y=50," +
            "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709," +
            "setpts=PTS+(RTCSTART-1700000000000000)/(1000000*TB)",
            "-copyts", "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "22", "-b:v", "0", "-g", "120",
            "-f", "matroska", "C:\\Rec Dir\\segment-01.mkv",
        }, args.Arguments);
        Assert.EndsWith(" \"C:\\Rec Dir\\segment-01.mkv\"", args.ToString());
    }

    [Fact]
    public void SegmentInSoftwareConvertsToYuv420OnTheCpu()
    {
        var args = RecordingSession.SegmentArguments(0, 30, new Int32Rect(0, 0, 1920, 1080), -1920, 0, 5, VideoEncoder.Software, "C:\\a.mkv").Arguments;

        Assert.Equal(
            "ddagrab=output_idx=0:framerate=30:video_size=1920x1080:offset_x=-1920:offset_y=0," +
            "hwdownload,format=bgra,scale=out_color_matrix=bt601:out_range=tv,format=yuv420p," +
            "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709," +
            "setpts=PTS+(RTCSTART-5)/(1000000*TB)", args[7]);
        Assert.Equal(new[] { "-copyts", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "120", "-f", "matroska", "C:\\a.mkv" }, args.Skip(8));
    }

    [Fact]
    public void EncoderArgumentsForLiveAndFileEncoding()
    {
        Assert.Equal(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "22", "-b:v", "0", "-g", "120" }, Ffmpeg.LiveVideoArgs(VideoEncoder.Nvenc));
        Assert.Equal(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "120" }, Ffmpeg.LiveVideoArgs(VideoEncoder.Software));
        Assert.Equal(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "21", "-b:v", "0", "-pix_fmt", "yuv420p" }, Ffmpeg.FileVideoArgs(VideoEncoder.Nvenc));
        Assert.Equal(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p" }, Ffmpeg.FileVideoArgs(VideoEncoder.Software));
    }

    [Theory]
    [InlineData(true, new[] { "-map", "0:a:0", "-c:a", "aac", "-b:a", "192k" })]
    [InlineData(false, new[] { "-an" })]
    public void Mp4ExportCutsReencodesAndReportsProgress(bool keepAudio, string[] audio)
    {
        var args = VideoExport.Mp4Arguments("C:\\My Videos\\in.mp4", 1.5, 4, keepAudio, VideoEncoder.Software, "C:\\My Videos\\in gekürzt.mp4").Arguments;

        var expected = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-ss", "1.5", "-to", "4", "-i", "C:\\My Videos\\in.mp4", "-map", "0:v:0" };
        expected.AddRange(audio);
        expected.AddRange(Ffmpeg.FileVideoArgs(VideoEncoder.Software));
        expected.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "C:\\My Videos\\in gekürzt.mp4" });
        Assert.Equal(expected, args);
    }

    [Fact]
    public void GifIsMadeInTwoPassesWithAPalette()
    {
        var palette = VideoExport.GifPaletteArguments("C:\\in.mp4", 2, 5, "C:\\Temp\\palette.png").Arguments;
        var gif = VideoExport.GifArguments("C:\\in.mp4", 2, 5, "C:\\Temp\\palette.png", "C:\\out.gif").Arguments;

        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-ss", "2", "-to", "5", "-i", "C:\\in.mp4",
            "-vf", "fps=15,scale='min(960,iw)':-2:flags=lanczos,palettegen=stats_mode=diff", "C:\\Temp\\palette.png",
        }, palette);
        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-ss", "2", "-to", "5", "-i", "C:\\in.mp4", "-i", "C:\\Temp\\palette.png",
            "-lavfi", "fps=15,scale='min(960,iw)':-2:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
            "-loop", "0", "-progress", "pipe:1", "-nostats", "C:\\out.gif",
        }, gif);
    }

    [Fact]
    public void SingleFrameSeeksAndLimitsTheWidth()
    {
        Assert.Equal(new[] { "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-ss", "2.5", "-i", "C:\\v.mp4", "-frames:v", "1", "-vf", "scale='min(640,iw)':-2", "C:\\f.png" },
            RecordedVideo.FrameArguments("C:\\v.mp4", 2.5, 640, "C:\\f.png").Arguments);
        Assert.Equal("0", RecordedVideo.FrameArguments("C:\\v.mp4", -1, 640, "C:\\f.png").Arguments[6]);
    }

    [Fact]
    public void FilmStripSeeksEveryFrameSeparatelyAndStacksThem()
    {
        var args = RecordedVideo.StripArguments("C:\\v.mp4", 6, 3, 60, "C:\\s.png").Arguments;

        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
            "-ss", "1", "-i", "C:\\v.mp4", "-ss", "3", "-i", "C:\\v.mp4", "-ss", "5", "-i", "C:\\v.mp4",
            "-filter_complex", "[0:v]scale=-2:60,setsar=1[v0];[1:v]scale=-2:60,setsar=1[v1];[2:v]scale=-2:60,setsar=1[v2];[v0][v1][v2]hstack=inputs=3",
            "-frames:v", "1", "C:\\s.png",
        }, args);
        Assert.EndsWith("[v0]null", RecordedVideo.StripArguments("C:\\v.mp4", 6, 1, 60, "C:\\s.png").Arguments[10]);
    }

    [Fact]
    public void ProgressLinesAreParsedAsSeconds()
    {
        Assert.Equal(1.5, FfmpegOutput.ProgressSeconds("out_time_us=1500000"));
        Assert.Null(FfmpegOutput.ProgressSeconds("out_time_us=N/A"));
        Assert.Null(FfmpegOutput.ProgressSeconds("out_time_us=-5"));
        Assert.Null(FfmpegOutput.ProgressSeconds("frame=12"));
    }

    [Fact]
    public void ProgressNeverGoesBackwardsAndStaysWithinZeroAndOne()
    {
        var reported = new List<double>();
        var progress = new MonotonicProgress(reported.Add);

        foreach (double value in new[] { -0.5, 0.2, 0.1, 0.2, 0.6, 1.4, 0.9 }) progress.Report(value);

        Assert.Equal(new[] { 0, 0.2, 0.2, 0.6, 1 }, reported);
    }
}
