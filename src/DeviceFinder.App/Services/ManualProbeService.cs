using System.Net;

namespace DeviceFinder.App.Services;

/// <summary>Résultat d'une sonde d'une IP manuelle.</summary>
/// <param name="Ip">IP sondée.</param>
/// <param name="Mac">MAC résolue via SendARP, null si aucune réponse ARP.</param>
/// <param name="PingOk">Vrai si l'IP répond au ping ICMP.</param>
public sealed record ManualProbeResult(string Ip, string? Mac, bool PingOk);

/// <summary>
/// Mode « IP manuelle » : l'utilisateur saisit une IP candidate (ou une petite
/// plage), l'application pose l'alias dans le sous-réseau puis sonde chaque
/// adresse (ping + SendARP). Rend l'outil pleinement utilisable sans Npcap.
/// </summary>
public static class ManualProbeService
{
    private const int MaxAddresses = 254;

    /// <summary>
    /// Analyse « a.b.c.d », « a.b.c.d-a.b.c.e » ou « a.b.c.d-x » (dernier octet).
    /// Toutes les adresses doivent être dans le même /24.
    /// </summary>
    public static List<string>? ParseRange(string text, out string? error)
    {
        error = null;
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            error = Loc.Get("Manual.EmptyInput");
            return null;
        }

        var parts = text.Split('-');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0].Trim(), out var first))
        {
            error = Loc.Fmt("Manual.InvalidRange", text);
            return null;
        }
        var firstBytes = first.GetAddressBytes();

        List<string> ips;
        if (parts.Length == 1)
        {
            ips = new List<string> { first.ToString() };
        }
        else
        {
            var secondText = parts[1].Trim();
            byte lastOctet;
            if (IPAddress.TryParse(secondText, out var second))
            {
                var secondBytes = second.GetAddressBytes();
                if (secondBytes[0] != firstBytes[0] || secondBytes[1] != firstBytes[1] || secondBytes[2] != firstBytes[2])
                {
                    error = Loc.Get("Manual.NotSameSubnet");
                    return null;
                }
                lastOctet = secondBytes[3];
            }
            else if (byte.TryParse(secondText, out var shorthand) && !secondText.Contains('.'))
            {
                // Forme « 192.168.1.50-60 » : dernier octet seul.
                lastOctet = shorthand;
            }
            else
            {
                error = Loc.Fmt("Manual.InvalidRangeEnd", secondText);
                return null;
            }

            if (lastOctet < firstBytes[3])
            {
                error = Loc.Get("Manual.EndBeforeStart");
                return null;
            }
            int count = lastOctet - firstBytes[3] + 1;
            if (count > MaxAddresses)
            {
                error = Loc.Fmt("Manual.RangeTooLarge", count, MaxAddresses);
                return null;
            }
            ips = new List<string>(count);
            for (byte host = firstBytes[3]; ; host++)
            {
                ips.Add(new IPAddress(new[] { firstBytes[0], firstBytes[1], firstBytes[2], host }).ToString());
                if (host == lastOctet)
                {
                    break;
                }
            }
        }

        foreach (var ip in ips)
        {
            if (LocalHostFilter.IsLocalIp(ip))
            {
                error = Loc.Fmt("Manual.IsLocalAddress", ip);
                return null;
            }
        }
        return ips;
    }

    /// <summary>
    /// Sonde chaque IP en parallèle : résolution MAC (SendARP) + ping ICMP.
    /// <paramref name="sourceIp"/> = IP alias posée sur la carte sélectionnée :
    /// SendARP sort alors uniquement sur cette carte (ciblage strict).
    /// </summary>
    public static async Task<List<ManualProbeResult>> ProbeAsync(IEnumerable<string> ips, IPAddress? sourceIp = null)
    {
        var tasks = ips.Select(async ip =>
        {
            var mac = SendArpResolver.TryResolve(IPAddress.Parse(ip), sourceIp);
            bool pingOk = await ReachabilityMonitor.PingAsync(ip, 800);
            return new ManualProbeResult(ip, mac is null ? null : SendArpResolver.FormatMac(mac), pingOk);
        });
        // WhenAll préserve l'ordre d'entrée (la plage est déjà triée).
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }
}
