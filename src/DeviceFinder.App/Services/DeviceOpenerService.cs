using System.Diagnostics;
using DeviceFinder.App.Models;

namespace DeviceFinder.App.Services;

public enum OpenOutcome
{
    OpenedDirect,
    OpenedAfterAlias,
    Failed,
    Cancelled,
}

public sealed record OpenResult(OpenOutcome Outcome, string? Url = null, string? AliasIp = null, string? Message = null)
{
    public static OpenResult Ok(OpenOutcome outcome, string url, string? aliasIp = null, string? message = null) =>
        new(outcome, url, aliasIp, message);
}

/// <summary>
/// Préparation de l'ouverture de la page web d'administration d'un device :
/// - si le device est joignable : ouverture directe ;
/// - sinon : ajout automatique (sans confirmation) d'une IP alias dans le sous-réseau
///   du device sur la carte Ethernet sélectionnée (idempotent : un alias existant dans
///   le /24 est réutilisé), puis nouvelle tentative de ping.
/// Le service construit l'URL finale (protocole et port propres au device) et la
/// retourne : le choix du navigateur (intégré WebView2 ou externe) appartient à l'UI.
/// Seul un conflit de sous-réseau avec une autre carte active demande une confirmation.
/// </summary>
public sealed class DeviceOpenerService
{
    private readonly AliasIpService _aliasService;

    public DeviceOpenerService(AliasIpService aliasService)
    {
        _aliasService = aliasService;
    }

    /// <summary>Construit l'URL d'administration d'un device (le port par défaut du protocole est omis).</summary>
    public static string BuildUrl(string ip, string protocol, int port)
    {
        var proto = protocol == "https" ? "https" : "http";
        int defaultPort = proto == "https" ? 443 : 80;
        var suffix = port > 0 && port != defaultPort ? $":{port}" : "";
        return $"{proto}://{ip}{suffix}/";
    }

    /// <param name="confirmSubnetConflict">Affiché à l'utilisateur ; retourne vrai pour continuer.</param>
    /// <param name="progress">Messages d'état affichés dans la barre de statut.</param>
    public async Task<OpenResult> OpenDeviceAsync(
        AdapterInfo adapter,
        Device device,
        string protocol,
        int port,
        Func<string, bool> confirmSubnetConflict,
        Action<string>? progress = null)
    {
        if (device.CurrentIp is null)
        {
            return new OpenResult(OpenOutcome.Failed,
                Message: Loc.Get("Open.NoIpYet"));
        }
        var deviceIp = device.CurrentIp;
        var url = BuildUrl(deviceIp, protocol, port);
        progress?.Invoke(Loc.Fmt("Open.TestingReachability", deviceIp));
        bool reachable = await ReachabilityMonitor.PingAsync(deviceIp, 900);

        string? aliasIp = null;
        if (!reachable)
        {
            var ensure = await Task.Run(() => _aliasService.EnsureAliasForSubnet(
                adapter, deviceIp, device.Mac, confirmSubnetConflict, progress));
            if (ensure.Cancelled)
            {
                return new OpenResult(OpenOutcome.Cancelled);
            }
            if (!ensure.Ok)
            {
                return new OpenResult(OpenOutcome.Failed, Message: ensure.Message ?? Loc.Get("Alias.EnsureFailed"));
            }
            aliasIp = ensure.AliasIp;

            // Laisser le stack ARP/ping prendre en compte l'alias, puis re-tester.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                progress?.Invoke(Loc.Fmt("Open.Retrying", attempt + 1));
                if (await ReachabilityMonitor.PingAsync(deviceIp, 900))
                {
                    reachable = true;
                    break;
                }
                await Task.Delay(700);
            }
        }

        return reachable
            ? new OpenResult(OpenOutcome.OpenedDirect, Url: url)
            : new OpenResult(OpenOutcome.OpenedAfterAlias, Url: url, AliasIp: aliasIp,
                Message: Loc.Fmt("Open.AliasAdded", aliasIp));
    }
}
