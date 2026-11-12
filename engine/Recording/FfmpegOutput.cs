namespace MosaikCapture.Engine.Recording;

/// <summary>Reading what ffmpeg and ffprobe print.</summary>
internal static class FfmpegOutput
{
    /// <summary>Number from ffprobe "name=value" lines (first occurrence); 0 if missing or N/A.</summary>
    public static double Field(string text, string name)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.Ordinal) &&
                double.TryParse(trimmed.Substring(name.Length + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return value;
        }
        return 0;
    }

    /// <summary>The last non-empty line, which is where ffmpeg puts the actual error.</summary>
    public static string LastLine(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length > 0 ? lines[lines.Length - 1] : UiText.Common.UnknownError;
    }

    /// <summary>Seconds from an "out_time_us=..." line of <c>-progress pipe:1</c>, or null for other lines.</summary>
    public static double? ProgressSeconds(string line) =>
        line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.Substring(12), out long micros) && micros >= 0
            ? micros / 1e6
            : null;
}
