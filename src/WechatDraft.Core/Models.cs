using System.Text.Json;
using System.Text.Json.Serialization;

namespace WechatDraft.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record AppSettings : IJsonOnDeserializing
{
    public List<ModelApiProfile> ApiProfiles { get; set; } = [];
    public string ApiProfileName { get; set; } = "默认 API";
    public string ActiveApiProfileId { get; set; } = "";
    public string DefaultApiProfileId { get; set; } = "";
    public string DefaultModelPackageHash { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.example.com/v1";
    public string ApiPath { get; set; } = "chat/completions";
    public string Model { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public string AuthorizedOrigin { get; set; } = "";
    public string VisionAuthorizedOrigin { get; set; } = "";
    public string AutomationMode { get; set; } = "vision";
    public string Scene { get; set; } = "春节";
    public string? ZodiacYear { get; set; }
    public string ExtraInstruction { get; set; } = "";
    public bool DemoMode { get; set; } = true;
    public bool DebugMode { get; set; }
    public bool AllowLoopbackHttp { get; set; }
    public bool EnableLiveAppend { get; set; } = true;
    public string AccountScope { get; set; } = "default";
    public string ExamplesPath { get; set; } = "";
    public int RetentionDays { get; set; } = 90;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int TaskTimeoutSeconds { get; set; } = 90;
    public uint HotkeyVirtualKey { get; set; } = 'A';
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;
    [JsonIgnore]
    public HotkeyShortcut GenerationHotkey => new(HotkeyModifiers, HotkeyVirtualKey);
    public WindowLayoutSettings? WindowLayout { get; set; }

    void IJsonOnDeserializing.OnDeserializing()
    {
        // Older saved settings can omit these fields. Explicit values are applied after this callback.
        AutomationMode = "auto";
        EnableLiveAppend = false;
        HotkeyVirtualKey = 'B';
        HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    }
}

public sealed record WindowLayoutSettings
{
    // Screen position is physical pixels; dimensions remain logical across display scaling changes.
    public int Left { get; init; }
    public int Top { get; init; }
    public double WidthDip { get; init; } = 840;
    public double HeightDip { get; init; } = 740;
    // Retained for compatibility with older settings; new launches always use normal windows.
    public bool Maximized { get; init; }
}

public sealed record Contact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AccountScope { get; init; } = "default";
    public string DisplayName { get; set; } = "";
    public string? RemarkName { get; set; }
    public string NameSource { get; set; } = "chat_title";
    public string Relationship { get; set; } = "unknown";
    public string? IdentityLabel { get; set; }
    public string? AddressTerm { get; set; }
    public string AddressTermSource { get; set; } = "unknown";
    public AddressEvidence? AddressEvidence { get; set; }
    public bool Confirmed { get; set; }
    public string BindingStatus { get; set; } = "candidate";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDemo { get; set; }
}

public sealed record TaskRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AccountScope { get; init; } = "default";
    public string? ContactId { get; set; }
    public bool ContactReused { get; set; }
    public string DisplayName { get; set; } = "";
    public string Scene { get; set; } = "";
    // Retain historical tone labels; new tasks choose their style from context.
    public string Tone { get; set; } = "auto";
    public string Relationship { get; set; } = "unknown";
    public string? IdentityLabel { get; set; }
    public string? AddressTerm { get; set; }
    public string AddressTermSource { get; set; } = "unknown";
    public AddressEvidence? AddressEvidence { get; set; }
    public string Greeting { get; set; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? GeneratedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string GenerationStatus { get; set; } = "未开始";
    public string FillStatus { get; set; } = "未尝试";
    public string Error { get; set; } = "";
    public string Model { get; set; } = "";
    public string ProviderOrigin { get; set; } = "";
    public int ScrollCount { get; set; }
    public int CaptureCount { get; set; }
    public int ApiCallCount { get; set; }
    public int LayoutApiCallCount { get; set; }
    public string AutomationMethod { get; set; } = "";
    public string FallbackReason { get; set; } = "";
    public string ExamplesVersion { get; set; } = "";
    public string[] ExampleIds { get; set; } = [];
    public string? AppendAttemptId { get; set; }
    public int OriginalCharacters { get; set; }
    public bool? OriginalTextRead { get; set; }
    public bool? OriginalPreserved { get; set; }
    public string? ObservedDisplayName { get; set; }
    public bool? ScreenshotRecipientMatched { get; set; }
    public bool IsDemo { get; set; }
    public string? DebugDirectory { get; set; }
}

public sealed record ExampleLibrary
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "1.0";
    [JsonPropertyName("library_version")] public string LibraryVersion { get; init; } = "";
    [JsonPropertyName("examples")] public List<BlessingExample> Examples { get; init; } = [];
}

public sealed record BlessingExample
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("scenes")] public string[] Scenes { get; init; } = [];
    [JsonPropertyName("relationship_types")] public string[] RelationshipTypes { get; init; } = [];
    [JsonPropertyName("tone")] public string Tone { get; init; } = "natural";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
}

public sealed record AddressEvidence
{
    [JsonPropertyName("source")] public string Source { get; init; } = "unknown";
    [JsonPropertyName("speaker")] public string Speaker { get; init; } = "unknown";
    [JsonPropertyName("quote")] public string Quote { get; init; } = "";
    [JsonPropertyName("capture_id")] public string CaptureId { get; init; } = "";
}

public sealed record ModelResponse
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "1.0";
    [JsonPropertyName("context_sufficient")] public bool ContextSufficient { get; init; }
    [JsonPropertyName("needs_more_context")] public bool NeedsMoreContext { get; init; }
    [JsonPropertyName("observed_display_name")] public string? ObservedDisplayName { get; init; }
    [JsonPropertyName("conversation_type")] public string ConversationType { get; init; } = "unknown";
    [JsonPropertyName("relationship_type")] public string RelationshipType { get; init; } = "unknown";
    [JsonPropertyName("identity_label")] public string? IdentityLabel { get; init; }
    [JsonPropertyName("address_term")] public string? AddressTerm { get; init; }
    [JsonPropertyName("address_evidence")] public AddressEvidence? AddressEvidence { get; init; }
    [JsonPropertyName("greeting_text")] public string GreetingText { get; init; } = "";
    [JsonPropertyName("warnings")] public string[] Warnings { get; init; } = [];
}

public sealed record Capture(string Id, byte[] PngBytes, bool RequiresConversationCheck = false, string? LocalTitle = null);
public sealed record ConversationSnapshot(string SessionToken, string DisplayName, string OriginalText,
    string WindowIdentity, bool IsDemo, object? NativeState = null, bool OriginalTextRead = true, bool DisplayNameRead = true, string TitleSource = "uia")
{
    public bool DirectAppend { get; init; }
}
public sealed record FillResult(string Status, string Message, bool? OriginalPreserved = null);
public sealed record TaskProgress(string Stage, string Detail, int Percent);

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IConversationAdapter
{
    bool CanAutoAppend => true;
    string AutoAppendUnavailableReason => "";
    bool CanCollectMoreContext => true;
    string AutomationMethod => "uia";
    string FallbackReason => "";
    string CleanupWarning => "";
    string StateChangeReason => "";
    int LayoutApiCallCount => 0;
    Task<ConversationSnapshot> BeginAsync(CancellationToken cancellationToken);
    Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken cancellationToken);
    Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken);
    Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken);
    Task<bool> WaitForAppendReadyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(true);
    Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken cancellationToken);
    Task RestoreAsync(ConversationSnapshot snapshot);
}

public interface IGreetingModel
{
    Task<ModelResponse> GenerateAsync(AppSettings settings, string apiKey, IReadOnlyList<Capture> captures,
        Contact? contact, IReadOnlyList<BlessingExample> examples, bool finalRound, CancellationToken cancellationToken);
}
