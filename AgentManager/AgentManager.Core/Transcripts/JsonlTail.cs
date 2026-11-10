namespace AgentManager.Core.Transcripts;

/// <summary>
/// Reads the lines appended to a JSONL file since the last call. A line still being written (no newline yet) is left for
/// the next call. Each yielded line is only valid until the iteration moves on.
/// </summary>
public sealed class JsonlTail
{
    private const int CHUNK_BYTES = 1 << 20;

    private long offset;

    public JsonlTail(string path, long startOffset = 0)
    {
        Path = path;
        offset = startOffset;
    }

    public string Path { get; }

    public long Offset => offset;

    public bool HasNewData()
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists && info.Length != offset;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public IEnumerable<ReadOnlyMemory<byte>> ReadNewLines()
    {
        var stream = TryOpenAppended();
        if (stream is null)
        {
            yield break;
        }

        using (stream)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[CHUNK_BYTES];
            var carry = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                while (start < read)
                {
                    var newline = buffer.AsSpan(start, read - start).IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        carry.Write(buffer, start, read - start);
                        break;
                    }

                    ReadOnlyMemory<byte> line;
                    if (carry.Length > 0)
                    {
                        carry.Write(buffer, start, newline);
                        line = carry.ToArray();
                        carry.SetLength(0);
                    }
                    else
                    {
                        line = buffer.AsMemory(start, newline);
                    }

                    offset += line.Length + 1;
                    start += newline + 1;
                    var trimmed = line.Span.Length > 0 && line.Span[^1] == (byte)'\r' ? line[..^1] : line;
                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }
            }
        }
    }

    private FileStream? TryOpenAppended()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length == offset)
            {
                return null;
            }

            if (info.Length < offset)
            {
                offset = 0;
            }

            return OpenShared(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static FileStream OpenShared(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
    }
}
