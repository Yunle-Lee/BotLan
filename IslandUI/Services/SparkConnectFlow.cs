using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace IslandUI.Services;

/// <summary>Wizard steps, in order. Install only appears when the Spark has no BotLan yet.</summary>
public enum SparkStep
{
    Ssh,
    Check,
    Install,
    Pair,
    Tunnel,
    AddBots,
}

public enum SparkStepState
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
}

public sealed class SparkFlowOptions
{
    public int PreferredLocalPort { get; init; } = 8091;
    public string KeyComment { get; init; } = SparkKeyStore.DefaultComment;
    public string? KeyPath { get; init; }
    /// <summary>Allow running the installer when BotLan is missing (wizard asks first).</summary>
    public bool AllowInstall { get; init; }
}

public sealed record SparkFlowResult(
    SparkConnection Connection,
    SparkPairingResult Pairing,
    IReadOnlyList<AgentConfig> NewBots,
    IReadOnlyList<SparkBotSpec> SkippedDuplicates,
    bool UsedKeyAuth);

/// <summary>Raised when BotLan is not installed on the Spark and AllowInstall was false.</summary>
public sealed class SparkNotInstalledException()
    : Exception("Spark 上还没有安装 BotLan。");

/// <summary>
/// The whole "连接 DGX Spark" sequence without UI: SSH → preflight → (install) → pair →
/// key install → tunnel → Bot plan. The caller adds the planned Bots to AgentStore.
/// </summary>
public sealed class SparkConnectFlow(SparkFlowOptions options)
{
    public event Action<SparkStep, SparkStepState, string?>? StepChanged;
    public event Action<string>? Log;

    private void Step(SparkStep s, SparkStepState st, string? detail = null) => StepChanged?.Invoke(s, st, detail);
    private void Say(string line) => Log?.Invoke(line);

    public async Task<SparkFlowResult> RunAsync(SparkTarget target, string? password,
        Func<IEnumerable<AgentConfig>> existingBots, CancellationToken ct = default)
    {
        var keyPath = options.KeyPath ?? SparkPaths.DefaultKeyFile;
        var saved = SparkConnectionStore.Find(target.Host, target.Port, target.User);
        SparkStep current = SparkStep.Ssh;
        try
        {
            // 1. SSH: saved key first, password as the fallback (first connect).
            Step(current, SparkStepState.Running, target.ToString());
            SparkSession? session = null;
            bool usedKey = false;
            if (SparkKeyStore.Exists(keyPath))
            {
                try
                {
                    session = await SparkConnector.ConnectAsync(target, null, keyPath, ct);
                    usedKey = true;
                    Say("已用本机密钥登录（无需密码）。");
                }
                catch (SparkException ex) when (!string.IsNullOrEmpty(password) && !ex.Message.Contains("指纹")
                                                 && ex.InnerException is Renci.SshNet.Common.SshAuthenticationException)
                {
                    Say("本机密钥未被接受，改用密码登录。");
                }
            }
            if (session == null && string.IsNullOrEmpty(password))
                throw new SparkException("首次连接这台 Spark 需要输入密码。");
            session ??= await SparkConnector.ConnectAsync(target, password, null, ct);

            using (session)
            {
                Say($"主机指纹 {session.Fingerprint}");
                Step(current, SparkStepState.Done, usedKey ? "密钥登录" : "密码登录");

                // 2. Preflight.
                current = SparkStep.Check;
                Step(current, SparkStepState.Running);
                var status = await SparkConnector.PreflightAsync(session, Say, ct);
                Say($"installed={status.Installed} gateway_ok={status.GatewayOk} models_ok={status.ModelsOk}");
                if (!status.Installed)
                {
                    if (!options.AllowInstall)
                    {
                        Step(current, SparkStepState.Failed, "未安装");
                        throw new SparkNotInstalledException();
                    }
                    Step(current, SparkStepState.Done, "未安装");
                    current = SparkStep.Install;
                    Step(current, SparkStepState.Running, "可能需要 15 分钟以上");
                    await SparkConnector.InstallAsync(session, Say, ct);
                    Step(current, SparkStepState.Done);
                    status = await SparkConnector.PreflightAsync(session, Say, ct);
                    if (!status.Installed) throw new SparkException("安装脚本已结束，但仍检测不到 BotLan。请查看日志。");
                    current = SparkStep.Check;
                }
                else
                {
                    Step(SparkStep.Install, SparkStepState.Skipped);
                }
                if (!status.GatewayOk)
                    throw new SparkException("Spark 上的 BotLan 网关没有运行。请在 Spark 上运行 systemctl --user restart botlan-gateway 后重试。");
                if (!status.ModelsOk) Say("提示：模型还没有就绪，Bot 可能要等模型加载完才能回复。");
                Step(SparkStep.Check, SparkStepState.Done, status.ModelsOk ? "网关正常" : "网关正常，模型未就绪");

                // 3. Pair.
                current = SparkStep.Pair;
                Step(current, SparkStepState.Running);
                var pairing = await SparkConnector.PairAsync(session, Say, ct);
                Say($"Spark {pairing.Spark.Hostname}：{pairing.Bots.Count} 个 Bot，网关端口 {pairing.Spark.GatewayPort}"
                    + (pairing.Spark.Gpu != null ? $"，{pairing.Spark.Gpu}" : "")
                    + (pairing.Spark.MemTotalGb is { } mem ? $"，{mem} GB" : ""));

                // Key-based reconnects from now on. The password is never stored.
                var publicKey = SparkKeyStore.EnsureKey(keyPath, options.KeyComment);
                await SparkConnector.InstallPublicKeyAsync(session, publicKey, ct);
                Say($"已在 Spark 上授权本机密钥（{options.KeyComment}）。");
                Step(current, SparkStepState.Done, $"{pairing.Bots.Count} 个 Bot");

                // 4. Tunnel.
                current = SparkStep.Tunnel;
                Step(current, SparkStepState.Running);
                var connection = new SparkConnection
                {
                    Host = target.Host,
                    Port = target.Port,
                    User = target.User,
                    GatewayPort = pairing.Spark.GatewayPort,
                    KeyPath = keyPath,
                    Fingerprint = session.Fingerprint,
                    Hostname = pairing.Spark.Hostname,
                    LocalPort = saved != null && saved.GatewayPort == pairing.Spark.GatewayPort
                        && SparkTunnelService.Instance.GetLocalPort(saved.Id) == saved.LocalPort
                        ? saved.LocalPort
                        : SparkTunnelService.PickLocalPort(options.PreferredLocalPort),
                };
                connection = SparkConnectionStore.Upsert(connection);
                if (SparkTunnelService.Instance.GetLocalPort(connection.Id) != connection.LocalPort)
                    SparkTunnelService.Instance.Start(connection);
                if (!await SparkTunnelService.Instance.WaitUpAsync(connection.Id, TimeSpan.FromSeconds(30), ct))
                    throw new SparkException("SSH 隧道没有建立起来。请检查 Spark 的 sshd 是否允许端口转发（AllowTcpForwarding）。");
                connection.LocalPort = SparkTunnelService.Instance.GetLocalPort(connection.Id) ?? connection.LocalPort;
                var health = await HealthAsync(connection.LocalPort, pairing.Bots[0].ApiKey, ct);
                if (health == HttpStatusCode.Unauthorized)
                    throw new SparkException("网关拒绝了配对得到的 API Key（401）。请在 Spark 上重新运行 sh scripts/09_setup.sh。");
                if (health != HttpStatusCode.OK)
                    throw new SparkException($"隧道已建立，但网关没有响应（{(health == 0 ? "无响应" : ((int)health).ToString())}）。");
                Step(current, SparkStepState.Done, $"127.0.0.1:{connection.LocalPort}");

                // 5. Bot plan.
                current = SparkStep.AddBots;
                Step(current, SparkStepState.Running);
                var (fresh, dupes) = PlanBots(pairing, connection, existingBots());
                return new SparkFlowResult(connection, pairing, fresh, dupes, usedKey);
            }
        }
        catch (SparkNotInstalledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var err = ex as SparkException ?? new SparkException(ex is OperationCanceledException ? "已取消。" : $"出错了：{ex.Message}", ex);
            Step(current, SparkStepState.Failed, err.Message);
            throw err;
        }
    }

    /// <summary>Pairing Bots as AgentConfigs, skipping ones that already exist (same base URL + key).</summary>
    public static (List<AgentConfig> Fresh, List<SparkBotSpec> Duplicates) PlanBots(
        SparkPairingResult pairing, SparkConnection connection, IEnumerable<AgentConfig> existing)
    {
        var have = existing.Select(c => (c.BaseUrl.TrimEnd('/').ToLowerInvariant(), c.ApiKey)).ToHashSet();
        var url = connection.BaseUrl;
        var fresh = new List<AgentConfig>();
        var dupes = new List<SparkBotSpec>();
        foreach (var b in pairing.Bots)
        {
            if (!have.Add((url.ToLowerInvariant(), b.ApiKey)))
            {
                dupes.Add(b);
                continue;
            }
            fresh.Add(new AgentConfig
            {
                Name = b.Name,
                BaseUrl = url,
                Model = b.Model,
                ApiKey = b.ApiKey,
                ColorHex = b.ColorHex,
                SparkConnectionId = connection.Id,
            });
        }
        return (fresh, dupes);
    }

    /// <summary>GET /health through the tunnel; 0 when nothing answered.</summary>
    public static async Task<HttpStatusCode> HealthAsync(int localPort, string? apiKey, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{localPort}/health");
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            return resp.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return 0;
        }
    }
}
