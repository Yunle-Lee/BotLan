using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IslandUI.Services;

/// <summary>A user-facing failure; Message is already a clear Chinese sentence.</summary>
public sealed class SparkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where Spark state lives. ISLANDUI_SPARK_DATA_DIR overrides it (tests).</summary>
public static class SparkPaths
{
    public static string DataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("ISLANDUI_SPARK_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandUI")
                : Path.GetFullPath(overrideDir);
        }
    }

    public static string ConnectionsFile => Path.Combine(DataDir, "sparks.json");
    public static string HostsFile => Path.Combine(DataDir, "spark-hosts.json");
    public static string DefaultKeyFile => Path.Combine(DataDir, "spark-key.dpapi");
}

/// <summary>One saved Spark. Holds no plaintext secret: the key file is DPAPI-encrypted.</summary>
public sealed class SparkConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    /// <summary>Laptop side of the tunnel (127.0.0.1).</summary>
    public int LocalPort { get; set; } = 8091;
    /// <summary>Gateway port on the Spark's 127.0.0.1.</summary>
    public int GatewayPort { get; set; } = 8091;
    public string KeyPath { get; set; } = "";
    /// <summary>SHA256 host key fingerprint pinned at first connect.</summary>
    public string Fingerprint { get; set; } = "";
    public string Hostname { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string BaseUrl => $"http://127.0.0.1:{LocalPort}/v1";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{User}@{Host}:{Port}";
}

/// <summary>sparks.json: the saved connections.</summary>
public static class SparkConnectionStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<SparkConnection> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(SparkPaths.ConnectionsFile)) return [];
                return JsonSerializer.Deserialize<List<SparkConnection>>(File.ReadAllText(SparkPaths.ConnectionsFile)) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public static SparkConnection? Find(string host, int port, string user)
        => Load().FirstOrDefault(c => string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                                      && c.Port == port && c.User == user);

    /// <summary>Insert or replace by host/port/user (keeps the existing Id).</summary>
    public static SparkConnection Upsert(SparkConnection connection)
    {
        lock (Gate)
        {
            var all = Load();
            var existing = all.FindIndex(c => string.Equals(c.Host, connection.Host, StringComparison.OrdinalIgnoreCase)
                                              && c.Port == connection.Port && c.User == connection.User);
            if (existing >= 0)
            {
                connection.Id = all[existing].Id;
                all[existing] = connection;
            }
            else
            {
                all.Add(connection);
            }
            Write(all);
            return connection;
        }
    }

    public static void UpdateLocalPort(string id, int localPort)
    {
        lock (Gate)
        {
            var all = Load();
            var c = all.FirstOrDefault(x => x.Id == id);
            if (c == null || c.LocalPort == localPort) return;
            c.LocalPort = localPort;
            Write(all);
        }
    }

    private static void Write(List<SparkConnection> all)
    {
        Directory.CreateDirectory(SparkPaths.DataDir);
        var tmp = SparkPaths.ConnectionsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, SparkPaths.ConnectionsFile, overwrite: true);
    }
}

/// <summary>Trust-on-first-use host keys, spark-hosts.json: "host:port" → "SHA256:…".</summary>
public static class SparkHostKeys
{
    private static readonly object Gate = new();

    public static string? Get(string host, int port)
    {
        lock (Gate)
            return LoadAll().TryGetValue(Key(host, port), out var fp) ? fp : null;
    }

    public static void Pin(string host, int port, string fingerprint)
    {
        lock (Gate)
        {
            var all = LoadAll();
            all[Key(host, port)] = fingerprint;
            Directory.CreateDirectory(SparkPaths.DataDir);
            File.WriteAllText(SparkPaths.HostsFile,
                JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    private static Dictionary<string, string> LoadAll()
    {
        try
        {
            if (File.Exists(SparkPaths.HostsFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SparkPaths.HostsFile)) ?? [];
        }
        catch
        {
        }
        return [];
    }
}

/// <summary>
/// The laptop's SSH identity for Sparks: RSA 3072, private key PEM encrypted with
/// DPAPI (CurrentUser) at rest. Only the public line ever leaves the machine.
/// </summary>
public static class SparkKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BotLan.SparkKey.v1");

    public static string DefaultComment
    {
        get
        {
            var machine = Regex.Replace(Environment.MachineName, "[^A-Za-z0-9._-]", "");
            return $"botlan@{(machine.Length == 0 ? "windows" : machine)}";
        }
    }

    public static bool Exists(string keyPath) => File.Exists(keyPath);

    /// <summary>Creates the key if missing and returns its OpenSSH public key line.</summary>
    public static string EnsureKey(string keyPath, string comment)
    {
        if (!File.Exists(keyPath))
        {
            using var rsa = RSA.Create(3072);
            var pem = Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem());
            try
            {
                var sealedBytes = ProtectedData.Protect(pem, Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllBytes(keyPath, sealedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }
        }
        return PublicKeyLine(keyPath, comment);
    }

    /// <summary>Decrypted PEM for SSH.NET; callers should not keep it around.</summary>
    public static byte[] LoadPem(string keyPath)
    {
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            throw new SparkException("本机保存的 Spark 密钥无法解密（可能来自其他 Windows 账户），请用密码重新连接。", ex);
        }
    }

    public static string PublicKeyLine(string keyPath, string comment)
    {
        var pem = LoadPem(keyPath);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(Encoding.ASCII.GetString(pem));
            var p = rsa.ExportParameters(false);
            using var ms = new MemoryStream();
            WriteString(ms, Encoding.ASCII.GetBytes("ssh-rsa"));
            WriteString(ms, MpInt(p.Exponent!));
            WriteString(ms, MpInt(p.Modulus!));
            return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} {comment}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    private static byte[] MpInt(byte[] unsignedBigEndian)
    {
        int skip = 0;
        while (skip < unsignedBigEndian.Length - 1 && unsignedBigEndian[skip] == 0) skip++;
        var trimmed = unsignedBigEndian[skip..];
        return (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
    }

    private static void WriteString(Stream s, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(data);
    }
}
