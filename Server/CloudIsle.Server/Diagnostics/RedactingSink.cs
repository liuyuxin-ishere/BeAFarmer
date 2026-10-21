using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace CloudIsle.Server.Diagnostics;

/// <summary>
/// 在事件进入控制台或文件之前改写它。password / token 属性、常见赋值文本和异常消息都会脱敏。
/// 不写出 Exception.ToString()。不识别属性名和这两种文本以外的秘密。
/// </summary>
sealed class RedactingSink : ILogEventSink, IDisposable
{
    static readonly Regex Assignment = new Regex(
        "(?i)\\b(password|token)\\b(\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|\\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex JsonPair = new Regex(
        "(?i)\"(password|token)\"(\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly ILogEventSink _inner;

    /// <summary>包住真正的接收器。本对象 Dispose 时会释放它。</summary>
    /// <param name="inner">已经脱敏之后才调用的接收器。为空时抛出异常。</param>
    public RedactingSink(ILogEventSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>脱敏后交给内层。内层抛出的异常原样冒泡，不在这里吞掉。</summary>
    /// <param name="logEvent">Serilog 原始事件。为空时抛出异常。</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null)
            throw new ArgumentNullException(nameof(logEvent));

        _inner.Emit(Sanitize(logEvent));
    }

    /// <summary>释放内层接收器，让文件 sink 刷出并关闭。</summary>
    public void Dispose()
    {
        (_inner as IDisposable)?.Dispose();
    }

    /// <summary>复制一份去掉原始异常、并改写过属性和模板文本的事件。</summary>
    /// <param name="logEvent">原始事件。</param>
    LogEvent Sanitize(LogEvent logEvent)
    {
        var properties = new List<LogEventProperty>(logEvent.Properties.Count + 2);
        foreach (var pair in logEvent.Properties)
            properties.Add(new LogEventProperty(pair.Key, RedactValue(pair.Value, pair.Key, 0)));

        if (logEvent.Exception != null)
        {
            AddIfMissing(properties, "ExceptionType", new ScalarValue(Classify(logEvent.Exception)));
            AddIfMissing(properties, "ExceptionMessage", new ScalarValue(RedactText(Describe(logEvent.Exception))));
        }

        var tokens = new List<MessageTemplateToken>();
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is TextToken text)
                tokens.Add(new TextToken(RedactText(text.Text)));
            else
                tokens.Add(token);
        }

        var candidate = new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            messageTemplate: new MessageTemplate(tokens),
            properties: properties);
        return ScrubRenderedSecrets(candidate);
    }

    /// <summary>
    /// 对渲染后才形成的 password= 或 token: 赋值做第二遍脱敏。
    /// 若模板把任意属性当作秘密值，保守遮住本事件所有被模板引用的属性；
    /// 消息保留脱敏后的可读文本。这样短值、数字或分段拼接不会残留在结构化属性中，
    /// 不依赖值长度或子串猜测。未被模板引用的关联属性仍保留。
    /// </summary>
    /// <param name="candidate">已按键名脱敏、且已去掉原始异常对象的事件。</param>
    static LogEvent ScrubRenderedSecrets(LogEvent candidate)
    {
        string rendered = candidate.RenderMessage(CultureInfo.InvariantCulture);
        string redacted = RedactText(rendered);
        if (rendered == redacted)
            return candidate;

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (MessageTemplateToken token in candidate.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property)
                referenced.Add(property.PropertyName);
        }

        var properties = new List<LogEventProperty>(candidate.Properties.Count);
        foreach (var pair in candidate.Properties)
        {
            properties.Add(new LogEventProperty(pair.Key,
                referenced.Contains(pair.Key) ? new ScalarValue("***") : pair.Value));
        }

        return new LogEvent(
            candidate.Timestamp,
            candidate.Level,
            exception: null,
            messageTemplate: new MessageTemplate(new[] { new TextToken(redacted) }),
            properties: properties);
    }

    /// <summary>按名字和嵌套值脱敏。秘密键整值替换，不再往下展开。</summary>
    /// <param name="value">当前值。</param>
    /// <param name="name">属性名或结构成员名。没有名字时为空。</param>
    /// <param name="depth">当前嵌套层。过深时不再展开。</param>
    static LogEventPropertyValue RedactValue(LogEventPropertyValue value, string? name, int depth)
    {
        if (IsSecretKey(name))
            return new ScalarValue("***");
        if (depth >= 16)
            return new ScalarValue("***");

        switch (value)
        {
            case ScalarValue scalar when scalar.Value is string text:
                return new ScalarValue(RedactText(text));
            case StructureValue structure:
                var members = new List<LogEventProperty>(structure.Properties.Count);
                foreach (var member in structure.Properties)
                    members.Add(new LogEventProperty(member.Name, RedactValue(member.Value, member.Name, depth + 1)));
                return new StructureValue(members, structure.TypeTag);
            case SequenceValue sequence:
                var items = new List<LogEventPropertyValue>(sequence.Elements.Count);
                foreach (var item in sequence.Elements)
                    items.Add(RedactValue(item, null, depth + 1));
                return new SequenceValue(items);
            case DictionaryValue dictionary:
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var entry in dictionary.Elements)
                {
                    string? key = entry.Key.Value as string;
                    var redactedKey = entry.Key.Value is string keyText
                        ? new ScalarValue(RedactText(keyText))
                        : entry.Key;
                    entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        redactedKey,
                        RedactValue(entry.Value, key, depth + 1)));
                }
                return new DictionaryValue(entries);
            default:
                return value;
        }
    }

    /// <summary>只把名为 password 或 token 的键视为秘密。比较忽略大小写，也认最后一个点或下划线后的名字。</summary>
    /// <param name="name">属性名。为空时不是秘密键。</param>
    static bool IsSecretKey(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        int split = name.LastIndexOfAny(new[] { '.', '_' });
        string leaf = split >= 0 ? name[(split + 1)..] : name;
        return leaf.Equals("password", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("token", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>替换 password=、token: 以及这两种键的 JSON 字符串。认不出的文本保持原样。</summary>
    /// <param name="text">可能含有秘密的文本。为空时原样返回。</param>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        string json = JsonPair.Replace(text, "\"$1\"$2\"***\"");
        return Assignment.Replace(json, "$1$2***");
    }

    /// <summary>异常类型全名。不包含消息和堆栈。</summary>
    /// <param name="exception">要分类的异常。</param>
    static string Classify(Exception exception)
    {
        var names = new List<string>();
        var seen = new HashSet<Exception>();
        for (Exception? current = exception; current != null && seen.Add(current); current = current.InnerException)
            names.Add(current.GetType().FullName ?? current.GetType().Name);
        return string.Join(" <- ", names);
    }

    /// <summary>只拼接异常消息。不调用 ToString，因此没有堆栈。</summary>
    /// <param name="exception">要描述的异常。</param>
    static string Describe(Exception exception)
    {
        var messages = new List<string>();
        var seen = new HashSet<Exception>();
        for (Exception? current = exception; current != null && seen.Add(current); current = current.InnerException)
            messages.Add(current.Message ?? "");
        return string.Join(" <- ", messages);
    }

    /// <summary>已经有同名属性时不覆盖。</summary>
    /// <param name="properties">正在组装的属性。</param>
    /// <param name="name">属性名。</param>
    /// <param name="value">属性值。</param>
    static void AddIfMissing(List<LogEventProperty> properties, string name, LogEventPropertyValue value)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            if (properties[i].Name == name)
                return;
        }

        properties.Add(new LogEventProperty(name, value));
    }
}

/// <summary>把同一份已脱敏事件交给控制台和文件。任一接收器抛错都会再抛出，另一路仍会尝试写完。</summary>
sealed class FanoutSink : ILogEventSink, IDisposable
{
    readonly ILogEventSink _console;
    readonly ILogEventSink _file;

    /// <summary>记下两个真正的 Serilog 接收器。</summary>
    /// <param name="console">控制台接收器。</param>
    /// <param name="file">滚动文件接收器。</param>
    public FanoutSink(ILogEventSink console, ILogEventSink file)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <summary>两边都写。先记下的异常在两边都尝试之后抛出。</summary>
    /// <param name="logEvent">已经脱敏的事件。</param>
    public void Emit(LogEvent logEvent)
    {
        Exception? error = null;
        try
        {
            _console.Emit(logEvent);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        try
        {
            _file.Emit(logEvent);
        }
        catch (Exception ex)
        {
            error ??= ex;
        }

        if (error != null)
            throw error;
    }

    /// <summary>关闭两个接收器。关闭文件接收器时会刷出缓冲。</summary>
    public void Dispose()
    {
        Exception? error = null;
        try
        {
            (_console as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        try
        {
            (_file as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            error ??= ex;
        }

        if (error != null)
            throw error;
    }
}
