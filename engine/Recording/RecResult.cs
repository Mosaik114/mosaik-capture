namespace MosaikCapture.Engine.Recording;

// Result: the finished file with its key data and a thumbnail.
class RecResult
{
    public string Path = "";
    public double Duration;
    public int Width, Height;
    public bool Audio, Gif;
    public long Bytes;
    public BitmapSource? Thumb;

    public static RecResult Load(string path)
    {
        var r = new RecResult { Path = path, Gif = string.Equals(System.IO.Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase) };
        FF.Run(FF.Probe, "-v error -show_entries format=duration:stream=codec_type,width,height -of default=nw=1 \"" + path + "\"", null, out string o, out string e);
        r.Duration = RecUi.Field(o, "duration");
        r.Width = (int)RecUi.Field(o, "width");
        r.Height = (int)RecUi.Field(o, "height");
        r.Audio = o.Contains("codec_type=audio");
        r.Bytes = new FileInfo(path).Length;
        r.Thumb = Frame(path, r.Duration / 2, 960);
        return r;
    }

    // A single frame from the video, at most maxW pixels wide.
    public static BitmapSource? Frame(string path, double at, int maxW)
    {
        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mosaiks-bild-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            if (FF.Run(FF.Exe, "-hide_banner -loglevel error -y -ss " + RecUi.Num(Math.Max(0, at)) + " -i \"" + path +
                "\" -frames:v 1 -vf \"scale='min(" + maxW + ",iw)':-2\" \"" + png + "\"", null, out string o, out string e) != 0 || !File.Exists(png)) return null;
            return RecUi.LoadImage(png);
        }
        finally { try { File.Delete(png); } catch (IOException) { } }   // temporary image in the temp folder
    }

    // Film strip for trimming: n frames side by side, h pixels high (fast: every frame uses its own seek).
    public static BitmapSource? Strip(string path, double dur, int n, int h)
    {
        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mosaiks-streifen-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var a = new StringBuilder("-hide_banner -loglevel error -y");
            var f = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                a.Append(" -ss " + RecUi.Num(dur * (i + 0.5) / n) + " -i \"" + path + "\"");
                f.Append("[" + i + ":v]scale=-2:" + h + ",setsar=1[v" + i + "];");
            }
            for (int i = 0; i < n; i++) f.Append("[v" + i + "]");
            f.Append(n > 1 ? "hstack=inputs=" + n : "null");
            a.Append(" -filter_complex \"" + f + "\" -frames:v 1 \"" + png + "\"");
            if (FF.Run(FF.Exe, a.ToString(), null, out string o, out string e) != 0 || !File.Exists(png)) return null;
            return RecUi.LoadImage(png);
        }
        finally { try { File.Delete(png); } catch (IOException) { } }   // temporary image in the temp folder
    }
}
