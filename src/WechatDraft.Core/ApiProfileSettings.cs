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
        return settings with { ApiProfiles = profiles, ActiveApiProfileId = profiles[active].Id };
    }

    // Editing any saved profile uses this snapshot: connection fields mirror the chosen
    // profile while ActiveApiProfileId still points at the profile the user selected.
    public static AppSettings EditForm(AppSettings settings, string profileId)
    {
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
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("请填写 API 配置名称。");
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

    private static ModelApiProfile Capture(AppSettings settings, string id, string name) => new()
    {
        Id = id, ApiProfileName = name, BaseUrl = settings.BaseUrl, ApiPath = settings.ApiPath,
        Model = settings.Model, ProtectedApiKey = settings.ProtectedApiKey,
        AllowLoopbackHttp = settings.AllowLoopbackHttp, AuthorizedOrigin = settings.AuthorizedOrigin,
        VisionAuthorizedOrigin = settings.VisionAuthorizedOrigin
    };
}
