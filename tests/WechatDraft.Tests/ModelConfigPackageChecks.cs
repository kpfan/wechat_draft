using System.Text;
using WechatDraft.Core;

internal static class ModelConfigPackageChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ISecretProtector protector)
    {
        const string sampleKey = "synthetic-credential-for-distribution-test";

        AppSettings Source(string origin) => ApiProfileSettings.Normalize(new()
        {
            BaseUrl = origin + "/v1", Model = "synthetic-vision", ProtectedApiKey = protector.Protect(sampleKey),
            AuthorizedOrigin = origin
        });

        string PublishedDirectory()
        {
            var directory = Path.Combine(root, "distribution-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "WechatDraft.exe"), []);
            return directory;
        }

        yield return ("A separately delivered encrypted package becomes the default without copying source consent", () =>
        {
            const string origin = "https://example.test";
            var app = PublishedDirectory();
            var file = Path.Combine(app, ModelConfigPackage.FileName);
            var source = Source(origin);
            var receiver = ApiProfileSettings.Normalize(new AppSettings { AccountScope = "recipient" });
            Equal(receiver, ModelConfigPackage.LoadDefault(receiver, app, protector));
            ModelConfigPackage.ExportDefault(source, app, file, protector);
            if (Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(sampleKey, StringComparison.Ordinal))
                throw new Exception("The distribution file contains a plaintext key.");
            var loaded = ModelConfigPackage.LoadDefault(receiver, app, protector);
            Equal(receiver.ActiveApiProfileId, loaded.ActiveApiProfileId);
            Equal("recipient", loaded.AccountScope);
            Equal(origin + "/v1", loaded.BaseUrl);
            Equal(sampleKey, protector.Unprotect(loaded.ProtectedApiKey));
            Equal("", loaded.AuthorizedOrigin);
            Reject(() => ApiProfileSettings.AuthorizeActiveProfile(loaded, loaded.ActiveApiProfileId, "https://other.test"));
            var authorized = ApiProfileSettings.AuthorizeActiveProfile(loaded, loaded.ActiveApiProfileId, origin);
            Equal(origin, authorized.AuthorizedOrigin);
            var repository = new LocalRepository(Path.Combine(app, "recipient-data"), protector);
            repository.SaveSettings(authorized);
            var restarted = repository.LoadSettings();
            if (!ReferenceEquals(restarted, ModelConfigPackage.LoadDefault(restarted, app, protector)))
                throw new Exception("The unchanged package should not be installed twice.");

            ModelConfigPackage.ExportDefault(source, app, file, protector);
            var refreshed = ModelConfigPackage.LoadDefault(restarted, app, protector);
            Equal(origin, refreshed.AuthorizedOrigin);
            var custom = ApiProfileSettings.Save(refreshed with { Model = "custom-model" }, null, "自定义");
            ModelConfigPackage.ExportDefault(Source("https://updated.example.test"), app, file, protector);
            var updated = ModelConfigPackage.LoadDefault(custom, app, protector);
            Equal(custom.ActiveApiProfileId, updated.ActiveApiProfileId);
            Equal("custom-model", updated.Model);
            var selected = ApiProfileSettings.Select(updated, updated.DefaultApiProfileId);
            Equal("https://updated.example.test/v1", selected.BaseUrl);
            Equal("", selected.AuthorizedOrigin);
            return Task.CompletedTask;
        });

        yield return ("Encrypted model configurations reject tampering and a mismatched software package", () =>
        {
            var first = PublishedDirectory();
            var second = PublishedDirectory();
            var file = Path.Combine(first, ModelConfigPackage.FileName);
            var otherFile = Path.Combine(second, ModelConfigPackage.FileName);
            var source = Source("https://example.test");
            ModelConfigPackage.ExportDefault(source, first, file, protector);
            ModelConfigPackage.ExportDefault(source, second, otherFile, protector);
            var receiver = ApiProfileSettings.Normalize(new AppSettings());

            var bytes = File.ReadAllBytes(file);
            File.WriteAllBytes(otherFile, bytes);
            Reject(() => ModelConfigPackage.LoadDefault(receiver, second, protector));
            bytes[^1] ^= 1;
            File.WriteAllBytes(file, bytes);
            Reject(() => ModelConfigPackage.LoadDefault(receiver, first, protector));
            Equal("", receiver.DefaultModelPackageHash);
            Equal("", receiver.ProtectedApiKey);
            return Task.CompletedTask;
        });
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception("Model distribution did not preserve the expected state.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid model distribution input was accepted.");
    }
}
