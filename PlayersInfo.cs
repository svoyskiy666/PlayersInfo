using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace PlayersInfo;

public class PlayersInfoPlugin : BasePlugin, IPluginConfig<PlayersInfoConfig>
{
    public override string ModuleName => "PlayersInfo";
    public override string ModuleVersion => "1.1.1";
    public override string ModuleAuthor => "pan1ka.su";
    public override string ModuleDescription =>
        "NEO monitoring PlayersInfo (порт Pisex mm_getinfo + mm_postpush)";

    public PlayersInfoConfig Config { get; set; } = new();

    private readonly ConcurrentDictionary<int, PlayerSession> _sessions = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly object _pushLock = new();
    private bool _pushInFlight;
    private RankService? _ranks;
    private FaceitService? _faceit;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoPush;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public void OnConfigParsed(PlayersInfoConfig config)
    {
        MergeConfig(config);
    }

    public override void Load(bool hotReload)
    {
        ApplyFileConfig();
        _ranks = new RankService(Config.Rank, Logger);
        _faceit = new FaceitService(Config.FaceitApiKey, Config.FaceitCacheMinutes, Logger, _http);

        // Имена как у Metamod-плагина — сайт дергает именно их по RCON
        AddCommand("mm_getinfo", "JSON сервера для мониторинга (RCON)", OnGetInfoCommand);
        AddCommand("css_getinfo", "JSON сервера для мониторинга (RCON)", OnGetInfoCommand);
        AddCommand("mm_postpush", "Пуш JSON на сайт мониторинга", OnPostPushCommand);
        AddCommand("css_postpush", "Пуш JSON на сайт мониторинга", OnPostPushCommand);
        AddCommand("css_playersinfo_reload", "Перечитать config.json", OnReloadCommand);

        RestartAutoPush();

        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                TrackPlayer(player);
            }
        }

        Logger.LogInformation(
            "PlayersInfo v{Ver}: ServerIp={Ip}, AutoPush={Sec}s, Faceit={Faceit}, Rank={Rank}",
            ModuleVersion,
            ResolveServerIp(),
            Config.AutoPushSeconds,
            _faceit.Enabled,
            Config.Rank.Enabled);
    }

    public override void Unload(bool hotReload)
    {
        _autoPush?.Kill();
        _http.Dispose();
        _sessions.Clear();
    }

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo info)
    {
        ApplyFileConfig();
        _ranks = new RankService(Config.Rank, Logger);
        _faceit = new FaceitService(Config.FaceitApiKey, Config.FaceitCacheMinutes, Logger, _http);
        RestartAutoPush();
        info.ReplyToCommand("PlayersInfo: config reloaded");
    }

    private void OnGetInfoCommand(CCSPlayerController? player, CommandInfo info)
    {
        try
        {
            var payload = BuildPayloadSync();
            info.ReplyToCommand(JsonSerializer.Serialize(payload, JsonOpts));
            DebugLog($"mm_getinfo -> {payload.PlayersCount} players, map={payload.CurrentMap}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PlayersInfo: mm_getinfo failed");
            info.ReplyToCommand("{\"error\":\"getinfo_failed\"}");
        }
    }

    private void OnPostPushCommand(CCSPlayerController? player, CommandInfo info)
    {
        info.ReplyToCommand("ok");
        _ = PushToWebsiteAsync("command");
    }

    private void RestartAutoPush()
    {
        _autoPush?.Kill();
        _autoPush = null;

        if (Config.AutoPushSeconds <= 0)
        {
            return;
        }

        var interval = Math.Max(5f, Config.AutoPushSeconds);
        _autoPush = AddTimer(interval, () => { _ = PushToWebsiteAsync("timer"); },
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        TrackPlayer(@event.Userid);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            _sessions.TryRemove(player.Slot, out _);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (Config.AutoPushSeconds > 0)
        {
            _ = PushToWebsiteAsync("round_start");
        }

        return HookResult.Continue;
    }

    private void TrackPlayer(CCSPlayerController? player)
    {
        if (!IsRealPlayer(player))
        {
            return;
        }

        var steam64 = player!.AuthorizedSteamID?.SteamId64 ?? 0;
        if (steam64 == 0)
        {
            steam64 = player.SteamID;
        }

        var session = new PlayerSession
        {
            Slot = player.Slot,
            // Pisex писал в JSON userid=slot; для админ-kick сайт ещё шлёт css_kick по steam
            UserId = player.Slot,
            SteamId64 = steam64,
            Name = player.PlayerName ?? "Unknown",
            ConnectedAt = DateTime.UtcNow,
            Prime = PrimeChecker.Check(steam64, Config.PrimeCheck, Logger)
        };

        _sessions[player.Slot] = session;

        if (steam64 != 0)
        {
            _ = WarmCachesAsync(session);
        }
    }

    private async Task WarmCachesAsync(PlayerSession session)
    {
        try
        {
            if (_ranks != null)
            {
                session.Rank = await _ranks.GetRankAsync(session.SteamId64);
            }

            if (_faceit != null)
            {
                session.FaceitLevel = await _faceit.GetLevelAsync(session.SteamId64);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "PlayersInfo: warm cache failed for {Steam}", session.SteamId64);
        }
    }

    private ServerPayload BuildPayloadSync()
    {
        var map = Server.MapName;
        if (string.IsNullOrWhiteSpace(map))
        {
            map = "-";
        }
        else
        {
            map = Path.GetFileNameWithoutExtension(map);
        }

        var maxPlayers = GetMaxPlayers();
        var (scoreCt, scoreT) = GetTeamScores();
        var players = new List<PlayerPayload>();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsRealPlayer(player))
            {
                continue;
            }

            if (!_sessions.TryGetValue(player.Slot, out var session))
            {
                TrackPlayer(player);
                _sessions.TryGetValue(player.Slot, out session);
            }

            session ??= new PlayerSession
            {
                Slot = player.Slot,
                UserId = player.Slot,
                SteamId64 = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID,
                Name = player.PlayerName ?? "Unknown",
                ConnectedAt = DateTime.UtcNow
            };

            session.Name = player.PlayerName ?? session.Name;
            session.UserId = player.Slot;
            session.Prime = PrimeChecker.Check(session.SteamId64, Config.PrimeCheck, Logger);

            var stats = player.ActionTrackingServices?.MatchStats;
            var playtime = (int)Math.Max(0, (DateTime.UtcNow - session.ConnectedAt).TotalSeconds);

            players.Add(new PlayerPayload
            {
                UserId = session.UserId,
                SteamId = session.SteamId64 > 0 ? session.SteamId64.ToString() : "",
                Name = session.Name,
                Team = (int)player.Team,
                Kills = stats?.Kills ?? 0,
                Death = stats?.Deaths ?? 0,
                Headshots = stats?.HeadShotKills ?? 0,
                Rank = session.Rank,
                FaceitLevel = session.FaceitLevel,
                Prime = session.Prime,
                Playtime = playtime,
                Ping = Math.Max(0, (int)player.Ping)
            });
        }

        return new ServerPayload
        {
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CurrentMap = map,
            MaxPlayers = maxPlayers,
            PlayersCount = players.Count,
            ScoreCt = scoreCt,
            ScoreT = scoreT,
            Players = players
        };
    }

    private async Task PushToWebsiteAsync(string reason)
    {
        lock (_pushLock)
        {
            if (_pushInFlight)
            {
                return;
            }

            _pushInFlight = true;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(Config.Password) || string.IsNullOrWhiteSpace(Config.WebsiteUrl))
            {
                DebugLog($"push skipped ({reason}): Password/WebsiteUrl пустой");
                return;
            }

            await RefreshSessionMetaAsync();

            ServerPayload? payload = null;
            var done = new ManualResetEventSlim(false);
            Server.NextFrame(() =>
            {
                try
                {
                    payload = BuildPayloadSync();
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(3000) || payload == null)
            {
                DebugLog($"push skipped ({reason}): не удалось собрать payload");
                return;
            }

            var serverIp = ResolveServerIp();
            if (string.IsNullOrWhiteSpace(serverIp) || serverIp.StartsWith("Unknown", StringComparison.Ordinal))
            {
                Logger.LogWarning("PlayersInfo: ServerIp не задан — укажите IP:PORT как на сайте");
                return;
            }

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, Config.WebsiteUrl);
            req.Headers.TryAddWithoutValidation("X-Server", serverIp);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.Password);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            DebugLog($"push ({reason}) {resp.StatusCode} ip={serverIp} players={payload.PlayersCount} body={Trim(body, 200)}");

            if (!resp.IsSuccessStatusCode)
            {
                Logger.LogWarning("PlayersInfo: push failed HTTP {Code}: {Body}", (int)resp.StatusCode, Trim(body, 300));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "PlayersInfo: push exception ({Reason})", reason);
        }
        finally
        {
            lock (_pushLock)
            {
                _pushInFlight = false;
            }
        }
    }

    private async Task RefreshSessionMetaAsync()
    {
        var tasks = new List<Task>();
        foreach (var session in _sessions.Values.ToArray())
        {
            if (session.SteamId64 == 0)
            {
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                if (_ranks != null && Config.Rank.Enabled)
                {
                    session.Rank = await _ranks.GetRankAsync(session.SteamId64);
                }

                if (_faceit != null && _faceit.Enabled && session.FaceitLevel == 0)
                {
                    session.FaceitLevel = await _faceit.GetLevelAsync(session.SteamId64);
                }
            }));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private string ResolveServerIp()
    {
        if (!string.IsNullOrWhiteSpace(Config.ServerIp))
        {
            return Config.ServerIp.Trim();
        }

        var ip = ConVar.Find("ip")?.StringValue;
        var port = ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "27015";
        if (string.IsNullOrWhiteSpace(ip) || ip is "0.0.0.0" or "localhost")
        {
            return $"Unknown:{port}";
        }

        return $"{ip}:{port}";
    }

    private static int GetMaxPlayers()
    {
        try
        {
            var visible = ConVar.Find("sv_visiblemaxplayers");
            if (visible != null)
            {
                var v = visible.GetPrimitiveValue<int>();
                if (v > 0)
                {
                    return v;
                }
            }
        }
        catch
        {
            // ignore
        }

        return Math.Max(1, Server.MaxPlayers);
    }

    private static (int Ct, int T) GetTeamScores()
    {
        try
        {
            var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
            var ct = teams.FirstOrDefault(t => t.Teamname == "CT")?.Score ?? 0;
            var t = teams.FirstOrDefault(t => t.Teamname == "TERRORIST")?.Score ?? 0;
            return (ct, t);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsRealPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsBot: false, IsHLTV: false };
    }

    private void ApplyFileConfig()
    {
        var path = Path.Combine(ModuleDirectory, "config.json");
        if (!File.Exists(path))
        {
            Logger.LogWarning("PlayersInfo: нет {Path} — используется дефолт / CSS config", path);
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PlayersInfoConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed != null)
            {
                MergeConfig(parsed);
                Logger.LogInformation("PlayersInfo: прочитан {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PlayersInfo: не удалось прочитать config.json");
        }
    }

    private void MergeConfig(PlayersInfoConfig incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.WebsiteUrl))
        {
            Config.WebsiteUrl = incoming.WebsiteUrl.Trim();
        }

        if (incoming.Password != null)
        {
            Config.Password = incoming.Password.Trim();
        }

        if (incoming.ServerIp != null)
        {
            Config.ServerIp = incoming.ServerIp.Trim();
        }

        Config.AutoPushSeconds = incoming.AutoPushSeconds;
        Config.Debug = incoming.Debug;
        Config.FaceitApiKey = incoming.FaceitApiKey?.Trim() ?? "";
        Config.FaceitCacheMinutes = incoming.FaceitCacheMinutes > 0 ? incoming.FaceitCacheMinutes : 180;
        Config.PrimeCheck = incoming.PrimeCheck;
        Config.Rank = incoming.Rank ?? new RankDbConfig();
    }

    private void DebugLog(string message)
    {
        if (Config.Debug)
        {
            Logger.LogInformation("PlayersInfo: {Msg}", message);
        }
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }
}
