using System.Security.Cryptography;
using System.Text.Json;

namespace WechatDraft.Core;

public static class ModelConfigPackage
{
    public const string FileName = "model-config.wdc";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4;
    private const int PrefixSize = HeaderSize + NonceSize + TagSize;
    private const int MaximumSize = 64 * 1024;
    private static ReadOnlySpan<byte> Header => "WDM1"u8;

    // This file is generated in the distribution directory, never embedded in source or binaries.
    private static string KeyPath(string appDirectory) => Path.Combine(appDirectory, "config", "model-config.key");

    private sealed record Connection(string BaseUrl, string ApiPath, string Model, string ApiKey, bool AllowLoopbackHttp);

    public static void ExportDefault(AppSettings settings, string appDirectory, string outputPath, ISecretProtector protector)
    {
        outputPath = Path.GetFullPath(outputPath);
        var profile = settings.ApiProfiles.SingleOrDefault(p => p.Id == settings.DefaultApiProfileId)
            ?? throw new InvalidDataException("源数据没有可导出的默认模型。");
        var connection = new Connection(profile.BaseUrl, profile.ApiPath, profile.Model,
            protector.Unprotect(profile.ProtectedApiKey), profile.AllowLoopbackHttp);
        Validate(connection);
        if (!File.Exists(Path.Combine(appDirectory, "WechatDraft.exe")))
            throw new InvalidDataException("请指定已发布的客户端目录。");
        if (!string.Equals(Path.GetFileName(outputPath), FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"配置文件必须命名为 {FileName}。");

        var keyPath = KeyPath(appDirectory);
        var key = ReadOrCreateKey(keyPath);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(connection, JsonDefaults.Options);
        try
        {
            if (plaintext.Length > MaximumSize - PrefixSize) throw new InvalidDataException("模型配置过大。");
            var package = new byte[PrefixSize + plaintext.Length];
            Header.CopyTo(package);
            var nonce = package.AsSpan(HeaderSize, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, package.AsSpan(PrefixSize), package.AsSpan(HeaderSize + NonceSize, TagSize), Header);
            WriteAtomically(outputPath, package, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static AppSettings LoadDefault(AppSettings settings, string appDirectory, ISecretProtector protector)
    {
        var path = Path.Combine(appDirectory, FileName);
        byte[] package;
        try { package = ReadBounded(path, MaximumSize); }
        catch (FileNotFoundException) { return settings; }
        if (package.Length <= PrefixSize || !package.AsSpan(0, HeaderSize).SequenceEqual(Header))
            throw new InvalidDataException("模型配置文件格式不正确。");

        var hash = Convert.ToHexString(SHA256.HashData(package));
        if (hash == settings.DefaultModelPackageHash &&
            settings.ApiProfiles.Any(p => p.Id == settings.DefaultApiProfileId && p.ProtectedApiKey.Length > 0))
            return settings;
        byte[] key;
        try { key = ReadKey(KeyPath(appDirectory)); }
        catch (FileNotFoundException) { throw new InvalidDataException("此软件包缺少模型配置解密文件。请使用与配置配套的软件包。"); }
        catch (DirectoryNotFoundException) { throw new InvalidDataException("此软件包缺少模型配置解密文件。请使用与配置配套的软件包。"); }
        var plaintext = new byte[package.Length - PrefixSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(package.AsSpan(HeaderSize, NonceSize), package.AsSpan(PrefixSize),
                package.AsSpan(HeaderSize + NonceSize, TagSize), plaintext, Header);
            var connection = JsonSerializer.Deserialize<Connection>(plaintext, JsonDefaults.Options)
                ?? throw new InvalidDataException("模型配置为空。");
            var origin = Validate(connection);
            return ApiProfileSettings.InstallDefault(settings, new ModelApiProfile
            {
                BaseUrl = connection.BaseUrl,
                ApiPath = connection.ApiPath,
                Model = connection.Model,
                ProtectedApiKey = protector.Protect(connection.ApiKey),
                AllowLoopbackHttp = connection.AllowLoopbackHttp
            }, origin, hash);
        }
        catch (AuthenticationTagMismatchException) { throw new InvalidDataException("模型配置与当前软件包不匹配，或文件已损坏。"); }
        catch (JsonException) { throw new InvalidDataException("模型配置文件内容无效。"); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string Validate(Connection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl) || string.IsNullOrWhiteSpace(connection.ApiPath) ||
            string.IsNullOrWhiteSpace(connection.Model) || string.IsNullOrWhiteSpace(connection.ApiKey) ||
            connection.ApiKey.Any(char.IsControl))
            throw new InvalidDataException("模型配置缺少有效的地址、接口路径、模型名或密钥。");
        var origin = DraftRules.Origin(new AppSettings
        {
            BaseUrl = connection.BaseUrl,
            ApiPath = connection.ApiPath,
            AllowLoopbackHttp = connection.AllowLoopbackHttp
        });
        if (origin != new Uri(connection.BaseUrl).GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("接口路径不能改变服务地址。");
        return origin;
    }

    private static byte[] ReadOrCreateKey(string path)
    {
        if (File.Exists(path)) return ReadKey(path);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            WriteAtomically(path, key, overwrite: false);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static byte[] ReadKey(string path)
    {
        var key = ReadBounded(path, KeySize);
        if (key.Length == KeySize) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidDataException("软件包的模型配置解密文件无效。");
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("模型配置文件大小不正确。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteAtomically(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
