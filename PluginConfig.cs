using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace PlayersInfo;

public class PlayersInfoConfig : BasePluginConfig
{
    /// <summary>Полный URL js_controller.php модуля мониторинга.</summary>
    public string WebsiteUrl { get; set; } =
        "https://pan1ka.su/app/modules/module_block_main_servers/includes/js_controller.php";

    /// <summary>Пароль из настроек мониторинга (Authorization: Bearer).</summary>
    public string Password { get; set; } = "";

    /// <summary>IP:PORT как в таблице мониторинга. Пусто = ip + hostport.</summary>
    public string ServerIp { get; set; } = "";

    /// <summary>Авто-пуш на сайт (сек). 0 = только по RCON mm_postpush.</summary>
    public float AutoPushSeconds { get; set; } = 15f;

    public bool Debug { get; set; } = false;

    /// <summary>Faceit Data API key. Пусто = faceit_level = 0.</summary>
    public string FaceitApiKey { get; set; } = "";

    public int FaceitCacheMinutes { get; set; } = 180;

    /// <summary>
    /// Как у Pisex Metamod: SteamGameServer UserHasLicenseForApp(624820|54029).
    /// В CSS работает только если в процессе есть Steamworks.NET (часто нет) — иначе false.
    /// </summary>
    public bool PrimeCheck { get; set; } = true;

    public RankDbConfig Rank { get; set; } = new();
}

public class RankDbConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "levels_ranks";
    public string Table { get; set; } = "lvl_base";
    public string SteamColumn { get; set; } = "steam";
    public string RankColumn { get; set; } = "rank";

    /// <summary>accountid | steam64 | steam2</summary>
    public string SteamFormat { get; set; } = "accountid";
}

public class PlayerSession
{
    public int Slot { get; set; }
    public int UserId { get; set; }
    public ulong SteamId64 { get; set; }
    public string Name { get; set; } = "";
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public int FaceitLevel { get; set; }
    public int Rank { get; set; }
    public bool Prime { get; set; }
}

public class PlayerPayload
{
    [JsonPropertyName("userid")]
    public int UserId { get; set; }

    [JsonPropertyName("steamid")]
    public string SteamId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("team")]
    public int Team { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("death")]
    public int Death { get; set; }

    [JsonPropertyName("headshots")]
    public int Headshots { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("faceit_level")]
    public int FaceitLevel { get; set; }

    [JsonPropertyName("prime")]
    public bool Prime { get; set; }

    [JsonPropertyName("playtime")]
    public int Playtime { get; set; }

    [JsonPropertyName("ping")]
    public int Ping { get; set; }
}

public class ServerPayload
{
    /// <summary>Как в Pisex GetServerInfo(): unix timestamp.</summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("current_map")]
    public string CurrentMap { get; set; } = "-";

    /// <summary>Дополнение для NEO (в старом Metamod не было, сайт fallback=32).</summary>
    [JsonPropertyName("max_players")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("players_count")]
    public int PlayersCount { get; set; }

    [JsonPropertyName("score_ct")]
    public int ScoreCt { get; set; }

    [JsonPropertyName("score_t")]
    public int ScoreT { get; set; }

    [JsonPropertyName("players")]
    public List<PlayerPayload> Players { get; set; } = new();
}
