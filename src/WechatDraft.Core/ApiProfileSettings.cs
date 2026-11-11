namespace WechatDraft.Core;

public sealed record ModelApiProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ApiProfileName { get; init; } = "默认 API";
    public string BaseUrl { get; init; } = "https://api.example.com/v1";
    public string ApiPath { get; init; } = "chat/completions";
    public string Model { get; init; } = "";
    public string ProtectedApiKey { get; init; } = "";
    public string AuthorizedOrigin { get; init; } = "";
    public string VisionAuthorizedOrigin { get; init; } = "";
    public bool AllowLoopbackHttp { get; init; }
}

public static class ApiProfileSettings
{
    public const string DefaultModelName = "默认模型";
    private const string LegacyDefaultName = "默认 API";

    // Keep the existing top-level connection fields as the active runtime configuration.
    // Older installations and developer configuration commands already write these fields.
    public static AppSettings Normalize(AppSettings settings)
    {
        var profiles = settings.ApiProfiles.ToList();
        var active = profiles.FindIndex(p => p.Id == settings.ActiveApiProfileId);
        if (active < 0) active = profiles.FindIndex(p => p.ApiProfileName == settings.ApiProfileName);
        if (active < 0)
        {
            profiles.Add(Capture(settings, Guid.NewGuid().ToString("N"), settings.ApiProfileName));
            active = profiles.Count - 1;
        }
        else profiles[active] = Capture(settings, profiles[active].Id, settings.ApiProfileName);
        var defaultProfile = profiles.Find(p => p.Id == settings.DefaultApiProfileId)
            ?? profiles.Find(p => p.ApiProfileName == LegacyDefaultName);
        return settings with
        {
            ApiProfiles = profiles,
            ActiveApiProfileId = profiles[active].Id,
            DefaultApiProfileId = defaultProfile?.Id ?? ""
        };
    }

    // Editing a custom profile uses this snapshot: connection fields mirror the chosen
    // profile while ActiveApiProfileId still points at the profile the user selected.
    public static AppSettings EditForm(AppSettings settings, string profileId)
    {
        RequireCustomProfile(settings, profileId);
        var profile = settings.ApiProfiles.SingleOrDefault(p => p.Id == profileId)
            ?? throw new InvalidDataException("API 配置已不存在，请刷新页面。");
        return settings with
        {
            BaseUrl = profile.BaseUrl, ApiPath = profile.ApiPath, Model = profile.Model,
            ProtectedApiKey = profile.ProtectedApiKey, AllowLoopbackHttp = profile.AllowLoopbackHttp,
            AuthorizedOrigin = profile.AuthorizedOrigin, VisionAuthorizedOrigin = profile.VisionAuthorizedOrigin
        };
    }

    public static AppSettings Save(AppSettings form, string? profileId, string name, bool activate = true)
    {
        RequireCustomProfile(form, profileId);
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("请填写 API 配置名称。");
        if (string.Equals(name, LegacyDefaultName, StringComparison.OrdinalIgnoreCase) || name == DefaultModelName)
            throw new InvalidDataException("该名称留给默认模型，请为自定义 API 使用其他名称。");
        if (form.ApiProfiles.Any(p => p.Id != profileId && string.Equals(p.ApiProfileName, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("已有同名 API 配置，请使用其他名称。");
        if (profileId is not null && !form.ApiProfiles.Any(p => p.Id == profileId))
            throw new InvalidDataException("API 配置已不存在，请重新打开设置。");
        var profile = Capture(form, profileId ?? Guid.NewGuid().ToString("N"), name);
        var profiles = form.ApiProfiles.ToList();
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        // Saving without activation never changes which profile is selected; the management
        // page switches profiles only through an explicit check. Editing the active profile
        // still refreshes the runtime snapshot so the next task uses the new values.
        if (activate || profile.Id == form.ActiveApiProfileId)
            return Select(form with { ApiProfiles = profiles, ActiveApiProfileId = profile.Id, ApiProfileName = name }, profile.Id);
        // The edit form mirrors the edited profile in the top-level connection fields;
        // restore the runtime snapshot from the active profile so editing never leaks them.
        return Select(form with { ApiProfiles = profiles }, form.ActiveApiProfileId);
    }

    public static AppSettings Delete(AppSettings settings, string profileId)
    {
        RequireCustomProfile(settings, profileId);
        var profile = settings.ApiProfiles.SingleOrDefault(p => p.Id == profileId)
            ?? throw new InvalidDataException("API 配置已不存在，请刷新页面。");
        if (settings.ApiProfiles.Count <= 1)
            throw new InvalidDataException("至少保留一组 API 配置。");
        if (profile.Id == settings.ActiveApiProfileId)
            throw new InvalidDataException("这是当前使用的 API，请先勾选其他配置再删除。");
        return settings with { ApiProfiles = settings.ApiProfiles.Where(p => p.Id != profileId).ToList() };
    }

    public static AppSettings Select(AppSettings settings, string profileId)
    {
        var profile = settings.ApiProfiles.SingleOrDefault(p => p.Id == profileId)
            ?? throw new InvalidDataException("API 配置已不存在，请重新打开设置。");
        return settings with
        {
            ActiveApiProfileId = profile.Id, ApiProfileName = profile.ApiProfileName,
            BaseUrl = profile.BaseUrl, ApiPath = profile.ApiPath, Model = profile.Model,
            ProtectedApiKey = profile.ProtectedApiKey, AllowLoopbackHttp = profile.AllowLoopbackHttp,
            AuthorizedOrigin = profile.AuthorizedOrigin, VisionAuthorizedOrigin = profile.VisionAuthorizedOrigin
        };
    }

    internal static AppSettings InstallDefault(AppSettings settings, ModelApiProfile profile, string origin, string packageHash)
    {
        var profiles = settings.ApiProfiles.ToList();
        var index = profiles.FindIndex(p => p.Id == settings.DefaultApiProfileId);
        var previous = index >= 0 ? profiles[index] : null;
        var updated = profile with
        {
            Id = previous?.Id ?? profile.Id,
            ApiProfileName = LegacyDefaultName,
            AuthorizedOrigin = previous?.AuthorizedOrigin == origin ? origin : ""
        };
        if (index >= 0) profiles[index] = updated;
        else profiles.Insert(0, updated);

        var candidate = settings with
        {
            ApiProfiles = profiles,
            DefaultApiProfileId = updated.Id,
            DefaultModelPackageHash = packageHash
        };
        return previous?.Id == settings.ActiveApiProfileId || settings.ActiveApiProfileId.Length == 0
            ? Select(candidate, updated.Id)
            : candidate;
    }

    public static AppSettings AuthorizeActiveProfile(AppSettings settings, string profileId, string origin)
    {
        if (settings.ActiveApiProfileId != profileId || DraftRules.Origin(settings) != origin)
            throw new InvalidDataException("当前 API 或服务地址已变更，请重新确认截图授权。");
        var profiles = settings.ApiProfiles.Select(p => p.Id == profileId
            ? p with { AuthorizedOrigin = origin } : p).ToList();
        return Select(settings with { ApiProfiles = profiles }, profileId);
    }

    private static void RequireCustomProfile(AppSettings settings, string? profileId)
    {
        if (settings.DefaultApiProfileId.Length > 0 && profileId == settings.DefaultApiProfileId)
            throw new InvalidDataException("默认模型仅供选择使用。");
    }

    private static ModelApiProfile Capture(AppSettings settings, string id, string name) => new()
    {
        Id = id, ApiProfileName = name, BaseUrl = settings.BaseUrl, ApiPath = settings.ApiPath,
        Model = settings.Model, ProtectedApiKey = settings.ProtectedApiKey,
        AllowLoopbackHttp = settings.AllowLoopbackHttp, AuthorizedOrigin = settings.AuthorizedOrigin,
        VisionAuthorizedOrigin = settings.VisionAuthorizedOrigin
    };
}
