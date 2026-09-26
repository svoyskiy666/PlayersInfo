using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlayersInfo;

public sealed class FaceitService
{
    private readonly string _apiKey;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<ulong, (int Level, DateTime At)> _cache = new();

    public FaceitService(string apiKey, int cacheMinutes, ILogger logger, HttpClient http)
    {
        _apiKey = apiKey?.Trim() ?? "";
        _cacheTtl = TimeSpan.FromMinutes(Math.Max(5, cacheMinutes));
        _logger = logger;
        _http = http;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<int> GetLevelAsync(ulong steam64)
    {
        if (!Enabled || steam64 == 0)
        {
            return 0;
        }

        if (_cache.TryGetValue(steam64, out var cached) && DateTime.UtcNow - cached.At < _cacheTtl)
        {
            return cached.Level;
        }

        try
        {
            var level = await FetchLevelAsync(steam64, "cs2");
            if (level == 0)
            {
                level = await FetchLevelAsync(steam64, "csgo");
            }

            _cache[steam64] = (level, DateTime.UtcNow);
            return level;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlayersInfo: Faceit API ошибка для {Steam}", steam64);
            return 0;
        }
    }

    private async Task<int> FetchLevelAsync(ulong steam64, string game)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://open.faceit.com/data/v4/players?game={game}&game_player_id={steam64}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            return 0;
        }

        return ParseLevel(await resp.Content.ReadAsStringAsync(), game);
    }

    private static int ParseLevel(string json, string game)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("games", out var games))
        {
            return 0;
        }

        if (!games.TryGetProperty(game, out var gameNode))
        {
            return 0;
        }

        if (gameNode.TryGetProperty("skill_level", out var skill) && skill.TryGetInt32(out var level))
        {
            return Math.Clamp(level, 0, 10);
        }

        return 0;
    }
}
