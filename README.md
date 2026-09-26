# PlayersInfo (CounterStrikeSharp)

Порт старого Metamod-плагина [Pisex/cs2-PlayersListCommand](https://github.com/Pisex/cs2-PlayersListCommand) под NEO-мониторинг.

Модули сайта не меняются. Команды те же: `mm_getinfo` (+ `mm_postpush` для HTTP-пуша NEO).

## Что было у Pisex

`mm_getinfo` → JSON:

| Поле | Источник |
|------|----------|
| `time` | unix timestamp |
| `current_map` | mapname |
| `score_ct` / `score_t` | cs_team_manager |
| `players[].userid` | **slot** (0..63) |
| `name`, `team`, `steamid` | контроллер |
| `kills` / `death` / `headshots` | MatchStats |
| `ping`, `playtime` | ping + время с connect |
| `prime` | `UserHasLicenseForApp(624820 \|\| 54029)` |
| `rank` | LevelsRanks `GetClientInfo(ST_RANK)` |

У Pisex **не было** `mm_postpush`, `max_players`, `faceit_level` — это нужно сайту NEO, поэтому добавлено.

## У нас (1.1.0)

- `mm_getinfo` / `css_getinfo` — JSON в RCON (как Pisex)
- `mm_postpush` / `css_postpush` — POST на `js_controller.php` (Bearer + `X-Server`)
- авто-пуш по таймеру
- ранг из MySQL LevelsRanks (аналог LR API)
- Faceit — опционально через API key
- Prime — та же логика AppID, если в процессе есть Steamworks.NET

## Установка

```bash
cd plugins/PlayersInfo
dotnet publish -c Release -o ./publish
```

На сервер:

```
addons/counterstrikesharp/plugins/PlayersInfo/PlayersInfo.dll
addons/counterstrikesharp/plugins/PlayersInfo/config.json
addons/counterstrikesharp/plugins/PlayersInfo/MySqlConnector.dll  # если Rank.Enabled
```

`config.json`:

```json
{
  "WebsiteUrl": "https://pan1ka.su/app/modules/module_block_main_servers/includes/js_controller.php",
  "Password": "пароль_мониторинга",
  "ServerIp": "45.95.31.99:27115",
  "AutoPushSeconds": 15,
  "Debug": true,
  "PrimeCheck": true,
  "FaceitApiKey": "",
  "Rank": {
    "Enabled": true,
    "Host": "127.0.0.1",
    "Port": 3306,
    "User": "lr",
    "Password": "",
    "Database": "levels_ranks",
    "Table": "lvl_base",
    "SteamColumn": "steam",
    "RankColumn": "rank",
    "SteamFormat": "accountid"
  }
}
```

Проверка: `rcon mm_getinfo` / `rcon mm_postpush`. Старый Metamod снять.
