namespace MosaikCapture.Engine.Recording;

/// <summary>
/// The last lines of a process's output, at most about <c>maxChars</c> characters: ffmpeg prints the
/// actual error at the end, so the oldest lines are dropped first. Thread-safe.
/// </summary>
internal sealed class TailBuffer
{
    const int LineBreak = 2;

    readonly int maxChars;
    readonly Queue<string> lines = new();
    int chars;

    public TailBuffer(int maxChars) => this.maxChars = maxChars;

    public bool IsEmpty
    {
        get { lock (lines) return lines.Count == 0; }
    }

    public void Add(string line)
    {
        lock (lines)
        {
            lines.Enqueue(line);
            chars += line.Length + LineBreak;
            while (chars > maxChars && lines.Count > 1) chars -= lines.Dequeue().Length + LineBreak;   // the newest line always stays
        }
    }

    public override string ToString()
    {
        lock (lines) return string.Join("\r\n", lines);
    }
}
