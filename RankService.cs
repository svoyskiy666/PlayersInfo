using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace PlayersInfo;

public sealed class RankService
{
    private readonly RankDbConfig _cfg;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, (int Rank, DateTime At)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public RankService(RankDbConfig cfg, ILogger logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<int> GetRankAsync(ulong steam64)
    {
        if (!_cfg.Enabled || steam64 == 0)
        {
            return 0;
        }

        if (_cache.TryGetValue(steam64, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Rank;
        }

        try
        {
            var steamKey = FormatSteam(steam64);
            var table = SanitizeIdent(_cfg.Table);
            var steamCol = SanitizeIdent(_cfg.SteamColumn);
            var rankCol = SanitizeIdent(_cfg.RankColumn);

            var csb = new MySqlConnectionStringBuilder
            {
                Server = _cfg.Host,
                Port = (uint)Math.Max(1, _cfg.Port),
                UserID = _cfg.User,
                Password = _cfg.Password,
                Database = _cfg.Database,
                ConnectionTimeout = 4,
                DefaultCommandTimeout = 4
            };

            await using var conn = new MySqlConnection(csb.ConnectionString);
            await conn.OpenAsync();

            var sql = $"SELECT `{rankCol}` FROM `{table}` WHERE `{steamCol}` = @steam LIMIT 1";
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@steam", steamKey);

            var result = await cmd.ExecuteScalarAsync();
            var rank = result == null || result is DBNull ? 0 : Convert.ToInt32(result);
            _cache[steam64] = (rank, DateTime.UtcNow);
            return rank;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlayersInfo: ошибка чтения ранга для {Steam}", steam64);
            return 0;
        }
    }

    private object FormatSteam(ulong steam64)
    {
        var format = (_cfg.SteamFormat ?? "accountid").Trim().ToLowerInvariant();
        return format switch
        {
            "steam64" => steam64.ToString(),
            "steam2" => ToSteam2(steam64),
            _ => (long)(steam64 - 76561197960265728UL)
        };
    }

    private static string ToSteam2(ulong steam64)
    {
        var account = steam64 - 76561197960265728UL;
        var y = account % 2;
        var z = account / 2;
        return $"STEAM_1:{y}:{z}";
    }

    private static string SanitizeIdent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Пустое имя таблицы/колонки");
        }

        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            {
                throw new InvalidOperationException($"Недопустимый идентификатор SQL: {value}");
            }
        }

        return value;
    }
}
