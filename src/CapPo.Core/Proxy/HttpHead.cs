using System.Text;

namespace CapPo.Core.Proxy;

/// <summary>An HTTP/1.x request or response head: the start line and the header fields, in order.</summary>
internal sealed class HttpHead
{
    private readonly List<KeyValuePair<string, string>> _headers;

    private HttpHead(string startLine, List<KeyValuePair<string, string>> headers)
    {
        StartLine = startLine;
        _headers = headers;
    }

    public string StartLine { get; set; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers => _headers;

    /// <summary>Parses the bytes before the blank line that ends a head (which isn't included).</summary>
    /// <exception cref="FormatException">Not an HTTP head.</exception>
    public static HttpHead Parse(ReadOnlySpan<byte> bytes)
    {
        // Header bytes are Latin-1 by RFC 9110; this round-trips any byte unchanged.
        var lines = Encoding.Latin1.GetString(bytes).Split("\r\n");
        if (lines.Length == 0 || lines[0].Length == 0)
            throw new FormatException("Empty request line.");

        List<KeyValuePair<string, string>> headers = [];
        foreach (var line in lines.AsSpan(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"Bad header line: {line}");
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new HttpHead(lines[0], headers);
    }

    public string? Get(string name) =>
        _headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public void Remove(string name) =>
        _headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Set(string name, string value)
    {
        Remove(name);
        _headers.Add(new(name, value));
    }

    /// <summary>The status code of a response head, e.g. 302; 0 when it isn't one.</summary>
    public int StatusCode =>
        StartLine.Split(' ', 3) is [_, var code, ..] && int.TryParse(code, out var status) ? status : 0;

    /// <summary>The head as sent on the wire, ending with the blank line.</summary>
    public byte[] ToBytes()
    {
        var text = new StringBuilder(StartLine).Append("\r\n");
        foreach (var (name, value) in _headers)
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        return Encoding.Latin1.GetBytes(text.Append("\r\n").ToString());
    }
}

/// <summary>Reads heads off a stream, keeping whatever came in after one for the body.</summary>
internal sealed class HeadReader(Stream stream, int maxHeadBytes = 64 * 1024)
{
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <returns>Null when the stream ended before any byte of a head.</returns>
    /// <exception cref="FormatException">The head is malformed, cut short or too long.</exception>
    public async Task<HttpHead?> ReadHeadAsync(CancellationToken cancellationToken)
    {
        // How far past _start has been searched already; relative, since MakeRoom moves the bytes.
        var searched = 0;
        while (true)
        {
            var from = _start + searched;
            var end = _buffer.AsSpan(from, _end - from).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                end += from;
                var head = HttpHead.Parse(_buffer.AsSpan(_start, end - _start));
                _start = end + 4;
                return head;
            }
            searched = Math.Max(0, _end - _start - 3);

            if (_end - _start >= maxHeadBytes)
                throw new FormatException("HTTP head too long.");
            MakeRoom();
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end == _start)
                    return null;
                throw new FormatException("Connection closed in the middle of an HTTP head.");
            }
            _end += read;
        }
    }

    /// <summary>Bytes read past the last head: the start of the body, or of a tunnel's traffic.</summary>
    public ReadOnlyMemory<byte> TakeBuffered()
    {
        var rest = _buffer.AsMemory(_start, _end - _start);
        _start = _end = 0;
        _buffer = new byte[_buffer.Length];
        return rest;
    }

    private void MakeRoom()
    {
        if (_end < _buffer.Length)
            return;
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
            return;
        }
        Array.Resize(ref _buffer, _buffer.Length * 2);
    }
}
