using System;
using System.IO;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Writes to an inner writer (the .acmi file) and hands the same text to a
/// secondary sink (the real-time host) on every flush, so the sink always
/// receives whole frames. A sink failure never affects the file.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly Action<string> _sink;
    private readonly Action<Exception> _onSinkError;
    private readonly StringBuilder _buffer = new StringBuilder(4096);
    private bool _sinkFailed;

    public TeeTextWriter(TextWriter inner, Action<string> sink, Action<Exception> onSinkError)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _onSinkError = onSinkError;
        base.NewLine = inner.NewLine;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override string NewLine
    {
        get => base.NewLine;
        set
        {
            base.NewLine = value;
            _inner.NewLine = value;
        }
    }

    public override void Write(char value)
    {
        _inner.Write(value);
        _buffer.Append(value);
    }

    public override void Write(string value)
    {
        _inner.Write(value);
        _buffer.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _inner.Write(buffer, index, count);
        _buffer.Append(buffer, index, count);
    }

    public override void WriteLine(string value)
    {
        Write(value);
        Write(NewLine);
    }

    public override void Flush()
    {
        _inner.Flush();
        if (_buffer.Length == 0 || _sinkFailed)
        {
            _buffer.Clear();
            return;
        }

        string text = _buffer.ToString();
        _buffer.Clear();
        try
        {
            _sink(text);
        }
        catch (Exception exception)
        {
            // The recording file matters more than the live view: stop teeing.
            _sinkFailed = true;
            _onSinkError?.Invoke(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
