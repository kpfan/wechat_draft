using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WechatDraft.Core;

// Opt-in diagnostics only. Failure to record must never change an input or retry decision.
public sealed class DebugSession : IDisposable
{
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const int MaximumItemBytes = 32 * 1024 * 1024;
    private static readonly AsyncLocal<DebugSession?> Ambient = new();
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Regex ImageData = new(@"data:image/[^;,\s""']+(?:;[^,\s""']*)?;base64,[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Bearer = new(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationLine = new(@"(\b(?:proxy-)?authorization[""']?\s*[:=]\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Assignment = new("(\\b(?:api[_ -]?key|protected[_ -]?api[_ -]?key|password|secret|access[_ -]?token|refresh[_ -]?token|token)\\b[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;\\r\\n}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuerySecret = new(@"([?&](?:api[_-]?key|key|access[_-]?token|token|secret)=)[^&#\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredentials = new(@"(https?://)[^/\s:@]+:[^/@\s]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private readonly DebugSession? _previous;
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly string _operation;
    private long _totalBytes;
    private int _sequence;
    private bool _enabled, _disposed;
    private string? _warning;

    public static bool IsSupported
    {
        get
        {
#if WECHAT_DRAFT_PRODUCTION
            return false;
#else
            return true;
#endif
        }
    }

    public static DebugSession? Current => Ambient.Value;
    public bool Enabled { get { lock (_sync) return _enabled && !_disposed; } }
    public string? DirectoryPath { get; private set; }
    public string? Warning { get { lock (_sync) return _warning; } }

    public static DebugSession Begin(bool enabled, string root, string operation) => new(enabled, root, operation);

    private DebugSession(bool enabled, string root, string operation)
    {
        _previous = Ambient.Value;
        Ambient.Value = this;
        _operation = operation;
        if (!enabled || !IsSupported) return;
        try
        {
            DirectoryPath = Path.Combine(Path.GetFullPath(root), _started.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            _enabled = true;
            WriteManifest(null);
            Event("session-start", new { operation });
        }
        catch (Exception e) { Warn("无法创建调试目录", e); _enabled = false; }
    }

    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (_sync)
        {
            if (!CanWrite()) return;
            try
            {
                _secrets.Add(secret);
                _secrets.Add(JsonEncodedText.Encode(secret).ToString());
                _secrets.Add(Uri.EscapeDataString(secret));
            }
            catch (Exception e) { Warn("无法配置调试脱敏", e); _enabled = false; }
        }
    }

    public void WriteJson(string kind, object? value)
    {
        lock (_sync)
        {
            if (!CanWrite()) return;
            try { Artifact(kind, JsonBytes(value), ".json"); }
            catch (Exception e) { Warn("无法记录调试 JSON", e); }
        }
    }

    public void WriteText(string kind, string text)
    {
        lock (_sync)
        {
            if (!CanWrite()) return;
            try
            {
                if (text.Length > MaximumItemBytes) { Warn("调试文本超过单文件上限"); return; }
                byte[] bytes;
                try
                {
                    using var json = JsonDocument.Parse(text);
                    bytes = JsonBytes(json.RootElement);
                }
                catch (JsonException) { bytes = Encoding.UTF8.GetBytes(RedactText(text)); }
                Artifact(kind, bytes, ".txt");
            }
            catch (Exception e) { Warn("无法记录调试文本", e); }
        }
    }

    public void WriteBytes(string kind, byte[] bytes, string extension)
    {
        lock (_sync)
        {
            if (!CanWrite()) return;
            try
            {
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                { Warn("调试二进制记录只允许 PNG 截图"); return; }
                Artifact(kind, bytes, ".png");
            }
            catch (Exception e) { Warn("无法记录调试截图", e); }
        }
    }

    public void Event(string stage, object? details = null)
    {
        lock (_sync)
        {
            if (!CanWrite()) return;
            try { Timeline(++_sequence, stage, details); }
            catch (Exception e) { Warn("无法记录调试阶段", e); }
        }
    }

    private void Artifact(string kind, byte[] bytes, string extension)
    {
        if (!SafeKind(kind) || _secrets.Any(secret => kind.Contains(secret, StringComparison.Ordinal)))
        { Warn("调试文件名无效或包含凭据，未写入"); return; }
        var number = ++_sequence;
        var name = number.ToString("D4") + "-" + kind + extension;
        if (Write(name, bytes)) Timeline(number, "artifact", new { file = name, kind, bytes = bytes.Length });
    }

    private void Timeline(int number, string stage, object? details)
    {
        var json = JsonBytes(new { sequence = number, utc = DateTimeOffset.UtcNow, stage, details }, indented: false);
        var line = new byte[json.Length + 1]; json.CopyTo(line, 0); line[^1] = (byte)'\n';
        Write("timeline.jsonl", line, append: true);
    }

    private bool Write(string name, byte[] bytes, bool append = false)
    {
        try
        {
            _sizes.TryGetValue(name, out var oldSize);
            var newSize = append ? oldSize + bytes.LongLength : bytes.LongLength;
            var limit = name == "manifest.json" ? MaximumBytes : MaximumBytes - 4096;
            if (newSize > MaximumItemBytes || _totalBytes - oldSize + newSize > limit)
            { Warn("调试记录达到容量上限（单文件32MiB、任务128MiB），后续超限文件未保存"); return false; }
            var path = Path.Combine(DirectoryPath!, name);
            if (append)
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            else File.WriteAllBytes(path, bytes);
            _totalBytes += newSize - oldSize; _sizes[name] = newSize;
            return true;
        }
        catch (Exception e) { Warn("无法写入调试文件", e); _enabled = false; return false; }
    }

    private byte[] JsonBytes(object? value, bool indented = true)
    {
        if (value is Exception exception) value = new { type = exception.GetType().Name, message = exception.Message };
        var element = value is JsonElement existing ? existing : JsonSerializer.SerializeToElement(value, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JsonOptions.Encoder }))
            RedactElement(writer, element);
        return stream.ToArray();
    }

    private void RedactElement(Utf8JsonWriter writer, JsonElement element, int embeddedDepth = 0)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(RedactText(property.Name, embeddedDepth));
                    if (SecretKey(property.Name)) writer.WriteStringValue("[redacted]");
                    else RedactElement(writer, property.Value, embeddedDepth);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) RedactElement(writer, item, embeddedDepth); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(RedactText(element.GetString() ?? "", embeddedDepth)); break;
            default: element.WriteTo(writer); break;
        }
    }

    private static bool SecretKey(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key.Contains("apikey", StringComparison.Ordinal) || key.Contains("authorization", StringComparison.Ordinal) ||
            key.Contains("password", StringComparison.Ordinal) || key.Contains("credential", StringComparison.Ordinal) ||
            key.Contains("secret", StringComparison.Ordinal) || key is "token" or "accesstoken" or "refreshtoken" or "idtoken" or "bearertoken";
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new ExceptionMetadataConverter());
        return options;
    }

    private sealed class ExceptionMetadataConverter : JsonConverter<Exception>
    {
        public override bool CanConvert(Type typeToConvert) => typeof(Exception).IsAssignableFrom(typeToConvert);
        public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteString("type", value.GetType().Name);
            writer.WriteString("message", value.Message); writer.WriteEndObject();
        }
    }

    private string RedactText(string text, int embeddedDepth = 0)
    {
        foreach (var secret in _secrets.OrderByDescending(value => value.Length)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            if (embeddedDepth >= 8) return "[nested JSON omitted: depth limit]";
            try
            {
                using var nested = JsonDocument.Parse(text);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JsonOptions.Encoder }))
                    RedactElement(writer, nested.RootElement, embeddedDepth + 1);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException) { /* Incomplete model text still receives literal and field-pattern redaction. */ }
        }
        text = ImageData.Replace(text, "[image omitted, see PNG]");
        text = AuthorizationLine.Replace(text, "$1[authentication redacted]");
        text = Bearer.Replace(text, "[authentication redacted]");
        text = Assignment.Replace(text, "$1[redacted]");
        text = QuerySecret.Replace(text, "$1[redacted]");
        return UrlCredentials.Replace(text, "$1[redacted]@");
    }

    private static bool SafeKind(string kind) => kind.Length is > 0 and <= 100 &&
        kind.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    private bool CanWrite() => _enabled && !_disposed;
    private void Warn(string message, Exception? error = null)
    {
        var detail = error is null ? "" : "（" + error.GetType().Name + "）";
        // Keep error details bounded and do not dump exception objects, paths, or credentials.
        _warning ??= message + detail;
    }

    private void WriteManifest(DateTimeOffset? finished) => Write("manifest.json", JsonBytes(new
    {
        formatVersion = "1.0", operation = _operation, startedUtc = _started, finishedUtc = finished,
        files = _sizes.Count, bytes = _totalBytes, warning = _warning,
        includesPersonalContent = true, authenticationRecorded = false
    }));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                if (_enabled)
                {
                    Event("session-end", new { warning = _warning });
                    WriteManifest(DateTimeOffset.UtcNow);
                }
            }
            catch (Exception e) { Warn("无法完成调试记录", e); }
            finally
            {
                _disposed = true;
                _secrets.Clear();
                if (ReferenceEquals(Ambient.Value, this))
                {
                    var previous = _previous;
                    while (previous is not null && previous._disposed) previous = previous._previous;
                    Ambient.Value = previous;
                }
            }
        }
    }
}
