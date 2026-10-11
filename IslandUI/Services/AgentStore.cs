using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace IslandUI.Services;

public enum AgentState
{
    Idle,
    Working,
    Notifying,   // finished / has something to say
    Error,
}

/// <summary>One configured agent: a model API endpoint plus display identity.</summary>
public sealed class AgentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Agent";
    public string BaseUrl { get; set; } = "http://127.0.0.1:8091/v1";
    public string Model { get; set; } = "jev-step";
    public string ApiKey { get; set; } = "";
    public string ColorHex { get; set; } = "#3ECF8E";
    public string BotShape { get; set; } = "cercle";
    public string BotExpression { get; set; } = "neutre";
    /// <summary>"quota" shows remaining API allowance on the ring; "status" shows work state.</summary>
    public string RingMode { get; set; } = "status";
    /// <summary>Set when the Bot came from "连接 DGX Spark"; its BaseUrl follows that tunnel's local port.</summary>
    public string? SparkConnectionId { get; set; }
}

/// <summary>A live agent bot on the long island.</summary>
public sealed class AgentBot : INotifyPropertyChanged
{
    private AgentState _state = AgentState.Idle;
    private string _activity = "";
    private double _ringFraction; // quota remaining or activity progress
    private string? _notification;

    public required AgentConfig Config { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentState State
    {
        get => _state;
        set { _state = value; Raise(); Raise(nameof(IsWorking)); Raise(nameof(BotStateName)); }
    }

    public bool IsWorking => State == AgentState.Working;

    /// <summary>Animation engine state name for this agent state.</summary>
    public string BotStateName => State switch
    {
        AgentState.Working => "thinking",
        AgentState.Notifying => "notify",
        AgentState.Error => "alert",
        _ => "idle",
    };

    /// <summary>Current activity line, e.g. "Thinking" / "Reading input.tsx".</summary>
    public string Activity
    {
        get => _activity;
        set { _activity = value; Raise(); }
    }

    /// <summary>0..1 value drawn on the bot's ring (quota remaining or activity).</summary>
    public double RingFraction
    {
        get => _ringFraction;
        set { _ringFraction = Math.Clamp(value, 0, 1); Raise(); }
    }

    /// <summary>Pending notification text (the bot "speaks" until dismissed).</summary>
    public string? Notification
    {
        get => _notification;
        set { _notification = value; Raise(); }
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Configured agents, persisted; live bots derived from them.</summary>
public sealed class AgentStore
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandUI");
    private static readonly string StorePath = Path.Combine(StoreDir, "agents.json");

    public static AgentStore Instance { get; } = new();

    private AgentStore() => Load();

    public ObservableCollection<AgentBot> Bots { get; } = [];

    public AgentBot AddAgent(AgentConfig config)
    {
        var bot = new AgentBot { Config = config };
        Bots.Add(bot);
        Save();
        return bot;
    }

    /// <summary>True when a Bot with the same endpoint and key already exists.</summary>
    public bool HasAgent(string baseUrl, string apiKey)
        => Bots.Any(b => string.Equals(b.Config.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                         && b.Config.ApiKey == apiKey);

    /// <summary>Points a Spark connection's Bots at its tunnel's current URL (local port changed).</summary>
    public void RetargetSparkBots(string connectionId, string newBaseUrl)
    {
        bool changed = false;
        foreach (var c in Bots.Select(b => b.Config).Where(c => c.SparkConnectionId == connectionId && c.BaseUrl != newBaseUrl))
        {
            c.BaseUrl = newBaseUrl;
            changed = true;
        }
        if (changed) Save();
    }

    public void RemoveAgent(AgentBot bot)
    {
        Bots.Remove(bot);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var configs = JsonSerializer.Deserialize<List<AgentConfig>>(File.ReadAllText(StorePath));
            if (configs == null) return;
            foreach (var c in configs)
                Bots.Add(new AgentBot { Config = c });
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(StorePath,
                JsonSerializer.Serialize(Bots.Select(b => b.Config).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
