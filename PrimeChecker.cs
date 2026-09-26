using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PlayersInfo;

/// <summary>
/// Порт CheckPrime из Pisex/cs2-PlayersListCommand:
/// SteamGameServer()->UserHasLicenseForApp(steamID, 624820|54029) == HasLicense.
/// </summary>
public static class PrimeChecker
{
    private const uint AppPrimeCs2 = 624820;
    private const uint AppPrimeCsgo = 54029;

    private static bool _resolved;
    private static MethodInfo? _userHasLicense;
    private static ConstructorInfo? _steamIdCtor;
    private static object? _hasLicenseEnumValue;
    private static Type? _appIdType;

    public static bool Check(ulong steam64, bool enabled, ILogger? logger = null)
    {
        if (!enabled || steam64 == 0)
        {
            return false;
        }

        try
        {
            EnsureResolved(logger);
            if (_userHasLicense == null || _steamIdCtor == null || _appIdType == null || _hasLicenseEnumValue == null)
            {
                return false;
            }

            var steamId = _steamIdCtor.Invoke(new object[] { steam64 });
            return HasLicense(steamId, AppPrimeCs2) || HasLicense(steamId, AppPrimeCsgo);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "PlayersInfo: Prime check failed for {Steam}", steam64);
            return false;
        }
    }

    private static bool HasLicense(object? steamId, uint appId)
    {
        if (steamId == null || _userHasLicense == null || _appIdType == null || _hasLicenseEnumValue == null)
        {
            return false;
        }

        object appIdObj;
        if (_appIdType == typeof(uint) || _appIdType == typeof(int))
        {
            appIdObj = Convert.ChangeType(appId, _appIdType);
        }
        else
        {
            // AppId_t struct with m_AppId or ctor(uint)
            var ctor = _appIdType.GetConstructor(new[] { typeof(uint) })
                       ?? _appIdType.GetConstructor(new[] { typeof(int) });
            if (ctor != null)
            {
                appIdObj = ctor.Invoke(new object[] { appId });
            }
            else
            {
                appIdObj = Activator.CreateInstance(_appIdType)!;
                var field = _appIdType.GetField("m_AppId") ?? _appIdType.GetField("Value");
                field?.SetValue(appIdObj, appId);
            }
        }

        var result = _userHasLicense.Invoke(null, new[] { steamId, appIdObj });
        return result != null && result.Equals(_hasLicenseEnumValue);
    }

    private static void EnsureResolved(ILogger? logger)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name ?? "";
            if (!name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var steamGameServer = asm.GetType("Steamworks.SteamGameServer");
            var cSteamId = asm.GetType("Steamworks.CSteamID");
            var appIdT = asm.GetType("Steamworks.AppId_t");
            var resultEnum = asm.GetType("Steamworks.EUserHasLicenseForAppResult");

            if (steamGameServer == null || cSteamId == null || resultEnum == null)
            {
                continue;
            }

            _userHasLicense = steamGameServer.GetMethod(
                "UserHasLicenseForApp",
                BindingFlags.Public | BindingFlags.Static);

            _steamIdCtor = cSteamId.GetConstructor(new[] { typeof(ulong) })
                           ?? cSteamId.GetConstructor(new[] { typeof(UInt64) });

            _appIdType = appIdT ?? typeof(uint);
            _hasLicenseEnumValue = Enum.Parse(resultEnum, "k_EUserHasLicenseResultHasLicense");

            logger?.LogInformation("PlayersInfo: Prime через Steamworks ({Asm})", name);
            return;
        }

        logger?.LogDebug("PlayersInfo: Steamworks не найден — prime всегда false (как без API)");
    }
}
