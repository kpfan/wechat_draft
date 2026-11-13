using System.Text.Json;

namespace WechatDraft.Core.ThirdVersion;

public sealed record ThirdVersionExtraction
{
    public string? Name { get; init; }
    public string Basis { get; init; } = "unknown";
    public string? Evidence { get; init; }
    public IReadOnlyList<AddressEvidence> EvidenceItems { get; init; } = Array.Empty<AddressEvidence>();
    public string? ObservedDisplayName { get; init; }
    public string Festival { get; init; } = "";
}

public sealed record ThirdVersionTurn(string Instruction, string Greeting, string Note);

public sealed record ThirdVersionRevision
{
    public string Greeting { get; init; } = "";
    public string Current => Greeting;
    public string Note { get; init; } = "";
    public IReadOnlyList<ThirdVersionTurn> Turns { get; init; } = Array.Empty<ThirdVersionTurn>();
}

/// <summary>实验会话仅保留内存数据，不包含任何写入微信的权限或操作。</summary>
public sealed class ThirdVersionSession
{
    private readonly AppSettings _settings;
    private readonly Capture[] _captures;
    private readonly Contact? _contact;
    private readonly GreetingGenerationContext _context;
    internal ThirdVersionSession(Guid owner, AppSettings settings, string key, IReadOnlyList<Capture> captures,
        Contact? contact, GreetingGenerationContext context)
    {
        Owner = owner;
        _settings = Clone(settings);
        _captures = captures.Select(x => x with { PngBytes = x.PngBytes.ToArray() }).ToArray();
        _contact = contact is null ? null : Clone(contact);
        _context = new(context.RecentGreetings.ToArray(), context.RejectedCandidate);
        Key = key;
    }

    public AppSettings Settings => Clone(_settings);
    public IReadOnlyList<Capture> Captures => Array.AsReadOnly(_captures.Select(x => x with { PngBytes = x.PngBytes.ToArray() }).ToArray());
    public Contact? Contact => _contact is null ? null : Clone(_contact);
    public GreetingGenerationContext Context => new(Array.AsReadOnly(_context.RecentGreetings.ToArray()), _context.RejectedCandidate);
    public ThirdVersionExtraction Extraction { get; internal set; } = new();
    public IReadOnlyList<string> Candidates { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<ThirdVersionRevision> Revisions { get; internal set; } = Array.Empty<ThirdVersionRevision>();
    public int ApiCallCount => Volatile.Read(ref CallCount);
    public string? ResolvedAddress { get; internal set; }
    public bool Neutral { get; internal set; }
    public bool IsDemo { get; internal set; }
    internal int CallCount;
    internal Guid Owner { get; }
    internal string Key { get; }
    internal AppSettings InputSettings => _settings;
    internal IReadOnlyList<Capture> InputCaptures => _captures;
    internal Contact? InputContact => _contact;
    internal GreetingGenerationContext InputContext => _context;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;
}
