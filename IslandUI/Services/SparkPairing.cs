using System.Text.Json;
using System.Text.RegularExpressions;

namespace IslandUI.Services;

/// <summary>What the Spark reports about itself during pairing.</summary>
public sealed record SparkInfo(string Hostname, int GatewayPort, bool GatewayOk, double? MemTotalGb, string? Gpu);

/// <summary>One Bot the Spark offers. Values are already validated and trimmed.</summary>
public sealed record SparkBotSpec(string Name, string Model, string ApiKey, string ColorHex, string? Backend);

public sealed record SparkPairingResult(SparkInfo Spark, IReadOnlyList<SparkBotSpec> Bots);

/// <summary>Result of <c>botlan_setup.py status --json</c> (or the master-key fallback).</summary>
public sealed record SparkStatus(bool Installed, bool GatewayOk, bool ModelsOk);

/// <summary>
/// Parses the Spark's pairing/status JSON. Remote output is untrusted: sizes are
/// capped, strings trimmed and length-limited, colours and keys pattern-checked.
/// </summary>
public static partial class SparkPairing
{
    public const int MaxPayloadChars = 256 * 1024;
    public const int MaxBots = 32;
    public const string DefaultColor = "#54A8FF";

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    // Gateway keys are opaque tokens: printable, no whitespace, bounded.
    [GeneratedRegex("^[A-Za-z0-9._~+/=:-]{8,256}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._:/@+-]{1,128}$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex HostnamePattern();

    /// <summary>Parses the pairing object. Throws <see cref="SparkException"/> on bad shape.</summary>
    public static SparkPairingResult ParsePair(string? text)
    {
        using var doc = ParseObject(text, "配对");
        var root = doc.RootElement;

        if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var version) && version > 1)
            throw new SparkException($"Spark 的配对格式版本 {version} 比本机新，请更新 BotLan。");

        if (!root.TryGetProperty("spark", out var sparkEl) || sparkEl.ValueKind != JsonValueKind.Object)
            throw new SparkException("配对信息缺少 spark 字段。");

        var hostname = Str(sparkEl, "hostname", 64) ?? "spark";
        if (!HostnamePattern().IsMatch(hostname)) hostname = "spark";
        int port = Int(sparkEl, "gateway_port") ?? 8091;
        if (port is < 1 or > 65535) throw new SparkException("配对信息里的网关端口无效。");
        var info = new SparkInfo(hostname, port,
            Bool(sparkEl, "gateway_ok") ?? false,
            Num(sparkEl, "mem_total_gb") is { } mem && mem is > 0 and < 100_000 ? Math.Round(mem, 1) : null,
            Str(sparkEl, "gpu", 64));

        var bots = new List<SparkBotSpec>();
        if (root.TryGetProperty("bots", out var botsEl) && botsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in botsEl.EnumerateArray())
            {
                if (bots.Count >= MaxBots) break;
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (ToBot(b, fallbackName: null) is { } spec) bots.Add(spec);
            }
        }

        if (bots.Count == 0 && root.TryGetProperty("master", out var master) && master.ValueKind == JsonValueKind.Object)
        {
            if (ToBot(master, fallbackName: "DGX Spark") is { } spec) bots.Add(spec);
        }

        if (bots.Count == 0)
            throw new SparkException("Spark 没有可用的 Bot（缺少 API Key）。请在 Spark 上运行 sh scripts/09_setup.sh 配置 Bot。");

        return new SparkPairingResult(info, bots);
    }

    /// <summary>Parses <c>status --json</c>.</summary>
    public static SparkStatus ParseStatus(string? text)
    {
        using var doc = ParseObject(text, "状态");
        var root = doc.RootElement;
        return new SparkStatus(
            Bool(root, "installed") ?? false,
            Bool(root, "gateway_ok") ?? false,
            Bool(root, "models_ok") ?? false);
    }

    private static SparkBotSpec? ToBot(JsonElement el, string? fallbackName)
    {
        // api_key: null (or missing/invalid) means the Bot is not usable from here.
        if (!el.TryGetProperty("api_key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) return null;
        var key = keyEl.GetString()!.Trim();
        if (!KeyPattern().IsMatch(key)) return null;

        var name = Str(el, "name", 40) ?? fallbackName ?? "Spark Bot";
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = fallbackName ?? "Spark Bot";

        var model = Str(el, "model", 128) ?? "jev-step";
        if (!ModelPattern().IsMatch(model)) model = "jev-step";

        var color = Str(el, "color", 7);
        if (color == null || !ColorPattern().IsMatch(color)) color = DefaultColor;

        var backend = Str(el, "backend", 40);
        return new SparkBotSpec(name, model, key, color.ToUpperInvariant(), backend);
    }

    /// <summary>Takes the last line that parses as a JSON object; tolerates banner noise before it.</summary>
    private static JsonDocument ParseObject(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SparkException($"Spark 没有返回{what}信息。");
        if (text.Length > MaxPayloadChars)
            throw new SparkException($"Spark 返回的{what}信息过大，已拒绝。");

        var trimmed = text.Trim();
        var candidates = new List<string> { trimmed };
        int brace = trimmed.IndexOf('{');
        if (brace > 0) candidates.Add(trimmed[brace..]);
        candidates.AddRange(trimmed.Split('\n').Reverse().Select(l => l.Trim()).Where(l => l.StartsWith('{')));

        foreach (var candidate in candidates)
        {
            try
            {
                var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 16 });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
                doc.Dispose();
            }
            catch (JsonException)
            {
            }
        }
        throw new SparkException($"Spark 返回的{what}信息不是有效的 JSON。");
    }

    private static string? Str(JsonElement el, string name, int max)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString()!.Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max].Trim() : s;
    }

    private static int? Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;

    private static double? Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
