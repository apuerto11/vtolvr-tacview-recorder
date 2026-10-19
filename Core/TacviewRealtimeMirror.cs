using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Keeps the complete current state of an ACMI text stream (header, global
/// properties and every live object) so a Real-Time Telemetry client that
/// connects mid-mission can receive a self-contained snapshot before the
/// incremental frames. The writer only emits changed fields; this mirror
/// accumulates them. Events are not replayed: they are instantaneous.
/// </summary>
public sealed class TacviewRealtimeMirror
{
    private const int TransformFieldCount = 9;

    private readonly List<string> _headerLines = new List<string>(2);
    private readonly Dictionary<string, string> _globals = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<string> _globalOrder = new List<string>();
    private readonly Dictionary<string, MirroredObject> _objects =
        new Dictionary<string, MirroredObject>(StringComparer.Ordinal);
    private readonly List<string> _objectOrder = new List<string>();
    private readonly StringBuilder _pending = new StringBuilder();
    private string _time;

    /// <summary>Number of live objects currently mirrored.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>True once an ACMI header has been seen.</summary>
    public bool HasSession => _headerLines.Count > 0;

    /// <summary>
    /// Applies ACMI text. Text may end mid-line; the remainder is kept until
    /// the next call completes it.
    /// </summary>
    public void Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _pending.Append(text);
        string buffered = _pending.ToString();
        int start = 0;
        int newline;
        while ((newline = buffered.IndexOf('\n', start)) >= 0)
        {
            ApplyLine(buffered.Substring(start, newline - start).TrimEnd('\r'));
            start = newline + 1;
        }

        _pending.Clear();
        _pending.Append(buffered, start, buffered.Length - start);
    }

    /// <summary>Forgets everything (no session).</summary>
    public void Reset()
    {
        _headerLines.Clear();
        _globals.Clear();
        _globalOrder.Clear();
        _objects.Clear();
        _objectOrder.Clear();
        _pending.Clear();
        _time = null;
    }

    /// <summary>
    /// Self-contained ACMI text describing the current state, or an empty
    /// string when no session has started.
    /// </summary>
    public string RenderSnapshot()
    {
        if (_headerLines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(256 + (_objects.Count * 160));
        for (int index = 0; index < _headerLines.Count; index++)
        {
            builder.Append(_headerLines[index]).Append('\n');
        }

        for (int index = 0; index < _globalOrder.Count; index++)
        {
            string key = _globalOrder[index];
            builder.Append("0,").Append(key).Append('=').Append(_globals[key]).Append('\n');
        }

        if (_time != null)
        {
            builder.Append('#').Append(_time).Append('\n');
        }

        for (int index = 0; index < _objectOrder.Count; index++)
        {
            MirroredObject mirrored = _objects[_objectOrder[index]];
            builder.Append(mirrored.Id);
            if (mirrored.HasTransform)
            {
                builder.Append(",T=");
                for (int field = 0; field < mirrored.TransformFieldsUsed; field++)
                {
                    if (field > 0)
                    {
                        builder.Append('|');
                    }

                    builder.Append(mirrored.Transform[field]);
                }
            }

            for (int property = 0; property < mirrored.PropertyOrder.Count; property++)
            {
                string key = mirrored.PropertyOrder[property];
                builder.Append(',').Append(key).Append('=').Append(mirrored.Properties[key]);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private void ApplyLine(string line)
    {
        if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        if (line[0] == '#')
        {
            _time = line.Substring(1);
            return;
        }

        if (line[0] == '-')
        {
            string removedId = NormalizeId(line.Substring(1));
            if (removedId != null && _objects.Remove(removedId))
            {
                _objectOrder.Remove(removedId);
            }

            return;
        }

        int comma = line.IndexOf(',');
        if (comma < 0)
        {
            if (line.StartsWith("FileType=", StringComparison.Ordinal))
            {
                // A new header starts a new session.
                Reset();
            }

            if (line.IndexOf('=') > 0)
            {
                _headerLines.Add(line);
            }

            return;
        }

        string id = NormalizeId(line.Substring(0, comma));
        if (id == null)
        {
            return;
        }

        List<KeyValuePair<string, string>> properties = SplitProperties(line, comma + 1);
        if (id == "0")
        {
            for (int index = 0; index < properties.Count; index++)
            {
                string key = properties[index].Key;
                if (key == "Event")
                {
                    continue;
                }

                if (!_globals.ContainsKey(key))
                {
                    _globalOrder.Add(key);
                }

                _globals[key] = properties[index].Value;
            }

            return;
        }

        if (!_objects.TryGetValue(id, out MirroredObject mirrored))
        {
            mirrored = new MirroredObject(id);
            _objects.Add(id, mirrored);
            _objectOrder.Add(id);
        }

        for (int index = 0; index < properties.Count; index++)
        {
            string key = properties[index].Key;
            string value = properties[index].Value;
            if (key == "T")
            {
                mirrored.ApplyTransform(value);
                continue;
            }

            if (!mirrored.Properties.ContainsKey(key))
            {
                mirrored.PropertyOrder.Add(key);
            }

            mirrored.Properties[key] = value;
        }
    }

    /// <summary>Splits <c>Key=Value</c> pairs on unescaped commas, keeping values escaped.</summary>
    private static List<KeyValuePair<string, string>> SplitProperties(string line, int start)
    {
        var result = new List<KeyValuePair<string, string>>();
        int tokenStart = start;
        for (int index = start; index <= line.Length; index++)
        {
            if (index < line.Length && line[index] == '\\')
            {
                index++;
                continue;
            }

            if (index < line.Length && line[index] != ',')
            {
                continue;
            }

            if (index > tokenStart)
            {
                string token = line.Substring(tokenStart, index - tokenStart);
                int equals = token.IndexOf('=');
                if (equals > 0)
                {
                    result.Add(new KeyValuePair<string, string>(
                        token.Substring(0, equals),
                        token.Substring(equals + 1)));
                }
            }

            tokenStart = index + 1;
        }

        return result;
    }

    private static string NormalizeId(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 ||
            trimmed.Length > 16 ||
            !ulong.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
        {
            return null;
        }

        return value.ToString("X", CultureInfo.InvariantCulture);
    }

    private sealed class MirroredObject
    {
        public MirroredObject(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public string[] Transform { get; } = new string[TransformFieldCount];

        public int TransformFieldsUsed { get; private set; }

        public bool HasTransform => TransformFieldsUsed > 0;

        public Dictionary<string, string> Properties { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public List<string> PropertyOrder { get; } = new List<string>();

        public void ApplyTransform(string value)
        {
            string[] fields = value.Split('|');
            int count = Math.Min(fields.Length, TransformFieldCount);
            for (int index = 0; index < count; index++)
            {
                // An empty field means "unchanged since the previous sample".
                if (fields[index].Length > 0)
                {
                    Transform[index] = fields[index];
                }
            }

            if (count > TransformFieldsUsed)
            {
                TransformFieldsUsed = count;
            }

            for (int index = 0; index < TransformFieldsUsed; index++)
            {
                if (Transform[index] == null)
                {
                    Transform[index] = string.Empty;
                }
            }
        }
    }
}
