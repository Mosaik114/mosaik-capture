namespace MosaikCapture.Engine.Recording;

/// <summary>
/// An ffmpeg/ffprobe command line built from individual arguments. Arguments are quoted only
/// when needed, following the rules Windows programs (CommandLineToArgvW) use to split the line,
/// so paths with spaces, quotes or trailing backslashes arrive unchanged.
/// </summary>
internal sealed class FfmpegArgs
{
    static readonly char[] NeedsQuotes = { ' ', '\t', '\n', '\v', '"' };
    readonly List<string> arguments = new();

    /// <summary>The usual start of every ffmpeg call: quiet, errors only, overwrite the output.</summary>
    public static FfmpegArgs Quiet() => new FfmpegArgs().Add("-hide_banner", "-loglevel", "error", "-y");

    public IReadOnlyList<string> Arguments => arguments;

    public FfmpegArgs Add(params string[] values)
    {
        arguments.AddRange(values);
        return this;
    }

    public FfmpegArgs Add(IEnumerable<string> values)
    {
        arguments.AddRange(values);
        return this;
    }

    /// <summary>Seconds or factors as ffmpeg expects them: always with a dot, at most six decimals.</summary>
    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public override string ToString() => string.Join(" ", arguments.Select(Quote));

    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(NeedsQuotes) < 0) return argument;
        var quoted = new StringBuilder("\"");
        for (int i = 0; i < argument.Length; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
            if (i == argument.Length)
            {
                quoted.Append('\\', backslashes * 2);   // keep them literal before the closing quote
                break;
            }
            if (argument[i] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(argument[i]);
        }
        return quoted.Append('"').ToString();
    }
}
