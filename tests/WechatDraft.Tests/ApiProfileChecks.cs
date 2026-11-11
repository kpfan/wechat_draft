using System.Text.Json;
using WechatDraft.Core;

internal static class ApiProfileChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ISecretProtector protector)
    {
        AppSettings Original(string profileName = "默认 API") => ApiProfileSettings.Normalize(new()
        {
            ApiProfileName = profileName,
            BaseUrl = "https://first.example/v1", Model = "first-vision", ProtectedApiKey = "encrypted-first",
            AuthorizedOrigin = "https://first.example", VisionAuthorizedOrigin = "https://first.example",
            AccountScope = "existing-account", ExtraInstruction = "保留补充要求", DemoMode = false,
            WindowLayout = new() { WidthDip = 1000, HeightDip = 800 }
        });
        AppSettings AddSecond(AppSettings first) => ApiProfileSettings.Save(first with
        {
            BaseUrl = "https://second.example/v1", Model = "second-vision", ProtectedApiKey = "encrypted-second",
            AuthorizedOrigin = "", VisionAuthorizedOrigin = "", AllowLoopbackHttp = true
        }, null, "备用 API");

        yield return ("Existing single API migrates with its credential and screenshot authorization intact", () =>
        {
            var settings = Original();
            Equal(1, settings.ApiProfiles.Count);
            Equal(settings.ActiveApiProfileId, settings.DefaultApiProfileId);
            var selected = ApiProfileSettings.Select(settings, settings.ActiveApiProfileId);
            Equal("encrypted-first", selected.ProtectedApiKey);
            Equal("https://first.example", selected.VisionAuthorizedOrigin);
            Equal(settings.ActiveApiProfileId, ApiProfileSettings.Normalize(settings).ActiveApiProfileId);
            return Task.CompletedTask;
        });
        yield return ("Adding and selecting APIs isolates credentials and authorization while preserving workspace settings", () =>
        {
            var first = Original(); var second = AddSecond(first);
            Equal(1, first.ApiProfiles.Count); Equal(2, second.ApiProfiles.Count);
            Equal("encrypted-second", second.ProtectedApiKey); Equal("", second.AuthorizedOrigin);
            var restored = ApiProfileSettings.Select(second, first.ActiveApiProfileId);
            Equal(first.BaseUrl, restored.BaseUrl); Equal(first.Model, restored.Model);
            Equal(first.ProtectedApiKey, restored.ProtectedApiKey);
            Equal(first.AuthorizedOrigin, restored.AuthorizedOrigin);
            Equal(first.VisionAuthorizedOrigin, restored.VisionAuthorizedOrigin);
            Equal(false, restored.AllowLoopbackHttp);
            Equal(first.AccountScope, restored.AccountScope); Equal(first.ExtraInstruction, restored.ExtraInstruction);
            Equal(first.DemoMode, restored.DemoMode); Equal(first.WindowLayout, restored.WindowLayout);
            return Task.CompletedTask;
        });
        yield return ("First-use screenshot consent works for a migrated default and stays with the confirmed API", () =>
        {
            const string origin = "https://first.example";
            var migrated = ApiProfileSettings.Normalize(Original() with { AuthorizedOrigin = "", VisionAuthorizedOrigin = "" });
            var defaultId = migrated.DefaultApiProfileId;
            var custom = ApiProfileSettings.Save(migrated, null, "同地址 API");
            var customId = custom.ActiveApiProfileId;

            Reject(() => ApiProfileSettings.AuthorizeActiveProfile(custom, defaultId, origin));
            Reject(() => ApiProfileSettings.AuthorizeActiveProfile(custom, customId, "https://other.example"));
            var current = ApiProfileSettings.AuthorizeActiveProfile(custom, customId, origin);
            Equal(origin, current.AuthorizedOrigin);
            Equal("", current.VisionAuthorizedOrigin);
            var selectedDefault = ApiProfileSettings.Select(current, defaultId);
            Equal("", selectedDefault.AuthorizedOrigin);
            Equal("", selectedDefault.DefaultModelPackageHash);
            var authorizedDefault = ApiProfileSettings.AuthorizeActiveProfile(selectedDefault, defaultId, origin);
            Equal(origin, authorizedDefault.AuthorizedOrigin);
            Equal(origin, ApiProfileSettings.Select(authorizedDefault, customId).AuthorizedOrigin);
            return Task.CompletedTask;
        });
        yield return ("Renaming an API keeps its identity and never creates an extra profile", () =>
        {
            var first = Original(); var second = AddSecond(first);
            var renamed = ApiProfileSettings.Save(second, second.ActiveApiProfileId, "  新名称  ");
            Equal(2, renamed.ApiProfiles.Count); Equal(second.ActiveApiProfileId, renamed.ActiveApiProfileId);
            Equal("新名称", renamed.ApiProfileName);
            Reject(() => ApiProfileSettings.Save(renamed, renamed.ActiveApiProfileId, first.ApiProfileName));
            Reject(() => ApiProfileSettings.Save(renamed, renamed.ActiveApiProfileId, "  "));
            Reject(() => ApiProfileSettings.Save(renamed, "missing-profile", "missing"));
            Reject(() => ApiProfileSettings.Select(renamed, "missing-profile"));
            return Task.CompletedTask;
        });
        yield return ("Deleting an active API credential cannot erase another API credential", () =>
        {
            var first = Original(); var second = AddSecond(first);
            var cleared = ApiProfileSettings.Save(second with { ProtectedApiKey = "" }, second.ActiveApiProfileId, second.ApiProfileName);
            Equal("", cleared.ProtectedApiKey);
            Equal("", ApiProfileSettings.Select(cleared, second.ActiveApiProfileId).ProtectedApiKey);
            Equal(first.ProtectedApiKey, ApiProfileSettings.Select(cleared, first.ActiveApiProfileId).ProtectedApiKey);
            Equal("encrypted-second", second.ProtectedApiKey);
            return Task.CompletedTask;
        });
        yield return ("Earlier name-based API lists migrate without nesting application settings", () =>
        {
            var legacy = JsonSerializer.Deserialize<AppSettings>("""
                {"baseUrl":"https://second.example/v1","model":"second","protectedApiKey":"encrypted-second",
                 "apiProfileName":"备用 API","apiProfiles":[
                    {"apiProfileName":"默认 API","baseUrl":"https://first.example/v1","protectedApiKey":"encrypted-first","apiProfiles":[]},
                    {"apiProfileName":"备用 API","baseUrl":"https://second.example/v1","protectedApiKey":"encrypted-second","apiProfiles":[]}]}
                """, JsonDefaults.Options)!;
            var migrated = ApiProfileSettings.Normalize(legacy);
            Equal(2, migrated.ApiProfiles.Count);
            Equal("备用 API", ApiProfileSettings.Select(migrated, migrated.ActiveApiProfileId).ApiProfileName);
            Equal(migrated.ApiProfiles[0].Id, migrated.DefaultApiProfileId);
            var json = JsonSerializer.Serialize(migrated.ApiProfiles, JsonDefaults.Options);
            if (json.Contains("apiProfiles") || json.Contains("accountScope")) throw new Exception("Profiles contain unrelated application settings.");
            return Task.CompletedTask;
        });
        yield return ("The default model remains selectable and read-only while a custom API is active", () =>
        {
            var first = Original();
            var second = AddSecond(first);
            var defaultId = first.ActiveApiProfileId;
            var reloaded = ApiProfileSettings.Normalize(second with { DefaultApiProfileId = "" });
            Equal(defaultId, reloaded.DefaultApiProfileId);
            Equal(second.ActiveApiProfileId, reloaded.ActiveApiProfileId);
            Reject(() => ApiProfileSettings.EditForm(reloaded, defaultId));
            Reject(() => ApiProfileSettings.Save(reloaded, defaultId, "改名"));
            Reject(() => ApiProfileSettings.Delete(reloaded, defaultId));
            Reject(() => ApiProfileSettings.Save(reloaded, null, ApiProfileSettings.DefaultModelName));
            var selected = ApiProfileSettings.Select(reloaded, defaultId);
            Equal(first.BaseUrl, selected.BaseUrl);
            Equal(first.Model, selected.Model);
            Equal(first.ProtectedApiKey, selected.ProtectedApiKey);
            Equal(first.AuthorizedOrigin, selected.AuthorizedOrigin);
            Equal(defaultId, ApiProfileSettings.Normalize(selected).DefaultApiProfileId);
            return Task.CompletedTask;
        });
        yield return ("Developer connection changes update the active profile without reviving an old endpoint", () =>
        {
            var second = AddSecond(Original());
            var updated = ApiProfileSettings.Normalize(second with { BaseUrl = "https://updated.example/v1", ProtectedApiKey = "encrypted-updated", AuthorizedOrigin = "" });
            var selected = ApiProfileSettings.Select(updated, second.ActiveApiProfileId);
            Equal("https://updated.example/v1", selected.BaseUrl); Equal("encrypted-updated", selected.ProtectedApiKey);
            Equal("", selected.AuthorizedOrigin); Equal(2, selected.ApiProfiles.Count);
            return Task.CompletedTask;
        });
        yield return ("Multiple API credentials and selected profile survive encrypted persistence and restart", () =>
        {
            var folder = Path.Combine(root, "api-profiles");
            var first = Original() with { ProtectedApiKey = protector.Protect("test-first-credential") };
            first = ApiProfileSettings.Normalize(first);
            var second = AddSecond(first) with { ProtectedApiKey = protector.Protect("test-second-credential") };
            second = ApiProfileSettings.Normalize(second);
            new LocalRepository(folder, protector).SaveSettings(second);
            var loaded = new LocalRepository(folder, protector).LoadSettings();
            Equal(2, loaded.ApiProfiles.Count); Equal(second.ActiveApiProfileId, loaded.ActiveApiProfileId);
            Equal(first.ActiveApiProfileId, loaded.DefaultApiProfileId);
            Equal("test-second-credential", protector.Unprotect(loaded.ProtectedApiKey));
            var selected = ApiProfileSettings.Select(loaded, first.ActiveApiProfileId);
            Equal("test-first-credential", protector.Unprotect(selected.ProtectedApiKey));
            new LocalRepository(folder, protector).SaveSettings(selected);
            Equal(first.ActiveApiProfileId, new LocalRepository(folder, protector).LoadSettings().ActiveApiProfileId);
            return Task.CompletedTask;
        });
        yield return ("Editing the active API from the management page refreshes the runtime without switching", () =>
        {
            var second = AddSecond(Original());
            var edited = ApiProfileSettings.Save(ApiProfileSettings.EditForm(second, second.ActiveApiProfileId) with
                { BaseUrl = "https://second-updated.example/v1", Model = "second-vision-2" },
                second.ActiveApiProfileId, second.ApiProfileName, activate: false);
            Equal(second.ActiveApiProfileId, edited.ActiveApiProfileId);
            Equal("https://second-updated.example/v1", edited.BaseUrl);
            Equal("second-vision-2", edited.Model);
            Equal("encrypted-second", edited.ProtectedApiKey);
            Equal("second-vision-2", edited.ApiProfiles.Single(p => p.Id == edited.ActiveApiProfileId).Model);
            Equal("first-vision", edited.ApiProfiles.Single(p => p.Id != edited.ActiveApiProfileId).Model);
            return Task.CompletedTask;
        });
        yield return ("Editing a saved API from the management page never switches the active endpoint", () =>
        {
            var first = Original("首个自定义 API"); var second = AddSecond(first);
            var inactiveId = first.ApiProfiles[0].Id;
            var form = ApiProfileSettings.EditForm(second, inactiveId);
            Equal(second.ActiveApiProfileId, form.ActiveApiProfileId);
            Equal(first.BaseUrl, form.BaseUrl);
            var edited = ApiProfileSettings.Save(form with { BaseUrl = "https://first-updated.example/v1", Model = "first-vision-2" },
                inactiveId, "默认 API 2", activate: false);
            Equal(second.ActiveApiProfileId, edited.ActiveApiProfileId);
            Equal(second.BaseUrl, edited.BaseUrl); Equal(second.Model, edited.Model);
            Equal("encrypted-second", edited.ProtectedApiKey);
            var updatedProfile = edited.ApiProfiles.Single(p => p.Id == inactiveId);
            Equal("first-vision-2", updatedProfile.Model); Equal("默认 API 2", updatedProfile.ApiProfileName);
            Equal("first-vision-2", ApiProfileSettings.Select(edited, inactiveId).Model);
            Reject(() => ApiProfileSettings.Save(edited, inactiveId, "备用 API"));
            return Task.CompletedTask;
        });
        yield return ("Deleting an API refuses the active or last profile and leaves the runtime untouched", () =>
        {
            var first = Original("首个自定义 API"); var second = AddSecond(first);
            var inactiveId = first.ApiProfiles[0].Id;
            Reject(() => ApiProfileSettings.Delete(second, second.ActiveApiProfileId));
            Reject(() => ApiProfileSettings.Delete(second, "missing-profile"));
            var removed = ApiProfileSettings.Delete(second, inactiveId);
            Equal(1, removed.ApiProfiles.Count);
            Equal(second.ActiveApiProfileId, removed.ActiveApiProfileId);
            Equal(second.ProtectedApiKey, removed.ProtectedApiKey);
            Equal(second.BaseUrl, removed.BaseUrl);
            Reject(() => ApiProfileSettings.Delete(removed, removed.ActiveApiProfileId));
            return Task.CompletedTask;
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid API configuration was accepted.");
    }
}
