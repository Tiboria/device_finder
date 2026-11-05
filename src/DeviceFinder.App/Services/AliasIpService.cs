using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace DeviceFinder.App.Services;

/// <summary>Résultat de la garantie d'un alias sur un sous-réseau.</summary>
/// <param name="Ok">Un alias est disponible dans le /24 (posé ou déjà existant).</param>
/// <param name="Cancelled">L'utilisateur a refusé de continuer (conflit de sous-réseau).</param>
/// <param name="AliasIp">L'adresse alias, si Ok.</param>
/// <param name="Message">Message d'état ou d'erreur.</param>
public sealed record AliasEnsureResult(bool Ok, bool Cancelled, string? AliasIp, string? Message);

/// <summary>
/// Gestion des IP alias posées par l'application sur la carte Ethernet sélectionnée :
/// ajout via netsh (store=active : jamais persistant) avec vérification que l'adresse
/// est bien active après pose (conflit détecté par Windows => retrait et candidat suivant),
/// traçabilité dans les paramètres, suppression. Aucune autre carte n'est modifiée.
/// </summary>
public sealed class AliasIpService
{
    private const int MaxAliasAttempts = 5;
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    /// <summary>
    /// Marqueur tracé dans <see cref="AliasRecord.DeviceMac"/> pour l'adresse IP du
    /// mini-serveur DHCP posée sur la carte : l'alias reste identifiable et supprimable
    /// séparément (à la désactivation du serveur), tout en étant couvert par le
    /// nettoyage global à la fermeture.
    /// </summary>
    public const string DhcpMarkerMac = "dhcp-serveur";

    public AliasIpService(AppSettings settings)
    {
        _settings = settings;
    }

    public event Action<string>? StatusMessage;

    /// <summary>
    /// Trouve la première adresse libre du /24 de deviceIp en partant de .100.
    /// « Libre » = personne ne répond à un SendARP sur cette adresse, ce n'est pas
    /// l'IP du device, pas une IP locale du PC et pas un candidat déjà essayé.
    /// </summary>
    public static string? FindFreeAliasIp(string deviceIp, IPAddress? sourceIp = null, ISet<string>? exclude = null)
    {
        if (!IPAddress.TryParse(deviceIp, out var device))
        {
            return null;
        }
        var bytes = device.GetAddressBytes();
        for (int host = 100; host <= 199; host++)
        {
            bytes[3] = (byte)host;
            var candidate = new IPAddress(bytes);
            var text = candidate.ToString();
            if (candidate.Equals(device))
            {
                continue;
            }
            if (exclude is not null && exclude.Contains(text))
            {
                continue;
            }
            if (LocalHostFilter.IsLocalIp(text))
            {
                continue;
            }
            var resident = SendArpResolver.TryResolve(candidate, sourceIp);
            if (resident is null)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>Alias déjà posé (tracé) dans le /24 de l'IP donnée, ou null.</summary>
    public AliasRecord? FindExistingAliasInSubnet(string targetIp)
    {
        if (!IPAddress.TryParse(targetIp, out var target))
        {
            return null;
        }
        var targetNet = NetworkAdapterService.ToUint(target) & NetworkAdapterService.MaskFromPrefix(24);
        lock (_gate)
        {
            return _settings.Aliases.FirstOrDefault(record =>
                IPAddress.TryParse(record.Ip, out var ip)
                && (NetworkAdapterService.ToUint(ip) & NetworkAdapterService.MaskFromPrefix(24)) == targetNet);
        }
    }

    /// <summary>
    /// Garantit qu'une IP alias existe sur la carte dans le /24 de targetIp :
    /// - réutilise un alias déjà posé dans ce /24 (double-clic idempotent) ;
    /// - sinon vérifie par SendARP que le candidat est libre, le pose, puis vérifie
    ///   qu'il est bien actif sur la carte (conflit Windows) ; en cas de refus,
    ///   retire l'alias et essaie le candidat suivant.
    /// </summary>
    public AliasEnsureResult EnsureAliasForSubnet(
        AdapterInfo adapter,
        string targetIp,
        string? deviceMac,
        Func<string, bool> confirmSubnetConflict,
        Action<string>? progress = null)
    {
        var existing = FindExistingAliasInSubnet(targetIp);
        if (existing is not null)
        {
            progress?.Invoke(Loc.Fmt("Alias.Reused", existing.Ip, adapter.Name));
            return new AliasEnsureResult(true, false, existing.Ip, null);
        }

        if (NetworkAdapterService.HasSubnetConflictOnOtherAdapter(adapter.Id, IPAddress.Parse(targetIp), out var conflict))
        {
            bool go = confirmSubnetConflict(
                Loc.Fmt("Conflict.AliasConfirm", conflict, targetIp, adapter.Name));
            if (!go)
            {
                return new AliasEnsureResult(false, true, null, null);
            }
        }

        var tried = new HashSet<string>();
        for (int attempt = 1; attempt <= MaxAliasAttempts; attempt++)
        {
            var candidate = FindFreeAliasIp(targetIp, adapter.PrimaryIpv4, tried);
            if (candidate is null)
            {
                return new AliasEnsureResult(false, false, null,
                    Loc.Fmt("Alias.NoFreeAddress", targetIp));
            }
            tried.Add(candidate);
            progress?.Invoke(Loc.Fmt("Alias.Adding", candidate, adapter.Name));
            if (AddAlias(adapter, candidate, deviceMac, verify: true))
            {
                return new AliasEnsureResult(true, false, candidate, null);
            }
            progress?.Invoke(
                Loc.Fmt("Alias.RejectedByWindows", candidate, attempt + 1, MaxAliasAttempts));
        }
        return new AliasEnsureResult(false, false, null,
            Loc.Get("Alias.EnsureFailed"));
    }

    /// <summary>
    /// Ajoute une IP alias (store=active) sur la carte sélectionnée et la trace dans les
    /// paramètres. Avec <paramref name="verify"/> : vérifie après un court délai que
    /// l'adresse est bien active sur la carte ; si Windows l'a refusée (adresse en
    /// double détectée), tente le nettoyage et échoue proprement.
    /// </summary>
    public bool AddAlias(AdapterInfo adapter, string aliasIp, string? deviceMac = null, bool verify = false)
    {
        var args = $"interface ipv4 add address name=\"{adapter.Name}\" address={aliasIp} mask=255.255.255.0 store=active";
        var (exitCode, output) = RunNetsh(args);
        if (exitCode != 0)
        {
            StatusMessage?.Invoke(Loc.Fmt("Alias.NetshFailed", exitCode, TrimOutput(output)));
            return false;
        }

        if (verify)
        {
            // Vérification DAD (Duplicate Address Detection) : Windows peut accepter la
            // commande netsh puis invalider l'adresse (doublon détecté sur le lien).
            // On attend que l'état se stabilise : Duplicate => retrait et candidat suivant.
            const int maxWaitMs = 4000;
            bool duplicate = false;
            bool present = false;
            for (int waited = 0; waited < maxWaitMs; waited += 250)
            {
                Thread.Sleep(250);
                switch (GetAddressState(adapter.Id, aliasIp))
                {
                    case AddressState.Active:
                        present = true;
                        break;
                    case AddressState.Duplicate:
                        duplicate = true;
                        break;
                    case AddressState.Tentative:
                    case AddressState.Absent:
                        present = false;
                        break;
                }
                if (present || duplicate)
                {
                    break;
                }
            }
            // Toujours Tentative après le délai maximal (lien muet, DAD sans conclusion) :
            // on garde l'adresse — seul un doublon explicite ou une absence est bloquant.
            if (!present && !duplicate && GetAddressState(adapter.Id, aliasIp) == AddressState.Tentative)
            {
                present = true;
            }
            if (duplicate || !present)
            {
                // L'adresse est refusée, en doublon ou introuvable sur la carte :
                // nettoyage et essai avec le candidat suivant.
                RunNetsh($"interface ipv4 delete address name=\"{adapter.Name}\" address={aliasIp} store=active");
                StatusMessage?.Invoke(duplicate
                    ? Loc.Fmt("Alias.DuplicateDetected", aliasIp)
                    : Loc.Fmt("Alias.NotActivated", aliasIp));
                return false;
            }
            LocalHostFilter.Refresh();
        }

        lock (_gate)
        {
            _settings.Aliases.Add(new AliasRecord
            {
                AdapterId = adapter.Id,
                AdapterName = adapter.Name,
                Ip = aliasIp,
                AddedUtc = DateTime.UtcNow,
                DeviceMac = deviceMac,
            });
            _settings.Save();
        }
        return true;
    }

    /// <summary>Supprime une IP alias (toutes cartes confondues : utilisé au nettoyage).</summary>
    public bool RemoveAlias(AliasRecord record)
    {
        if (string.IsNullOrEmpty(record.AdapterName) || string.IsNullOrEmpty(record.Ip))
        {
            return false;
        }
        // On tente la suppression active et persistante pour couvrir les résidus éventuels.
        RunNetsh($"interface ipv4 delete address name=\"{record.AdapterName}\" address={record.Ip} store=active");
        var (exitCode, output) = RunNetsh(
            $"interface ipv4 delete address name=\"{record.AdapterName}\" address={record.Ip} store=persistent");
        bool success = exitCode == 0;
        if (!success)
        {
            // Une adresse ajoutée en store=active peut ne pas exister en store=persistent : pas une erreur.
            StatusMessage?.Invoke(Loc.Fmt("Alias.RemoveFailed", record.Ip, TrimOutput(output)));
        }
        LocalHostFilter.Refresh();
        return true;
    }

    /// <summary>Supprime toutes les IP alias tracées et vide la liste.</summary>
    public void RemoveAllAliases()
    {
        lock (_gate)
        {
            foreach (var record in _settings.Aliases.ToList())
            {
                try
                {
                    RemoveAlias(record);
                }
                catch (Exception ex)
                {
                    StatusMessage?.Invoke(Loc.Fmt("Alias.DeleteFailed", record.Ip, ex.Message));
                }
            }
            _settings.Aliases.Clear();
            _settings.Save();
        }
    }

    /// <summary>
    /// Supprime uniquement les IP alias tracées portant ce marqueur (ex. l'adresse du
    /// mini-serveur DHCP), sans toucher aux alias posés pour des devices.
    /// </summary>
    public void RemoveAliasesWithMac(string markerMac)
    {
        List<AliasRecord> records;
        lock (_gate)
        {
            records = _settings.Aliases
                .Where(r => string.Equals(r.DeviceMac, markerMac, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        foreach (var record in records)
        {
            try
            {
                RemoveAlias(record);
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke(Loc.Fmt("Alias.DeleteFailed", record.Ip, ex.Message));
            }
        }
        lock (_gate)
        {
            _settings.Aliases.RemoveAll(r =>
                string.Equals(r.DeviceMac, markerMac, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
        }
    }

    public IReadOnlyList<AliasRecord> GetAliases()
    {
        lock (_gate)
        {
            return _settings.Aliases.ToList();
        }
    }

    private enum AddressState
    {
        Absent,
        Tentative,
        Active,
        Duplicate,
    }

    /// <summary>
    /// État DAD d'une adresse sur une carte : Absent (non configurée),
    /// Tentative (DAD en cours), Active (DAD réussi), Duplicate (conflit détecté par Windows).
    /// </summary>
    private static AddressState GetAddressState(string adapterId, string ip)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == adapterId);
            if (nic is null)
            {
                return AddressState.Absent;
            }
            var address = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          && a.Address.ToString() == ip);
            if (address is null)
            {
                return AddressState.Absent;
            }
            var state = address.DuplicateAddressDetectionState;
            if (state == System.Net.NetworkInformation.DuplicateAddressDetectionState.Duplicate
                || state == System.Net.NetworkInformation.DuplicateAddressDetectionState.Invalid)
            {
                return AddressState.Duplicate;
            }
            if (state == System.Net.NetworkInformation.DuplicateAddressDetectionState.Tentative)
            {
                return AddressState.Tentative;
            }
            return AddressState.Active;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Alias: check of {ip} on {adapterId} failed: {ex.Message}");
            return AddressState.Absent;
        }
    }

    /// <summary>Vrai si l'adresse est actuellement configurée sur la carte donnée.</summary>
    private static bool IsAddressOnAdapter(string adapterId, string ip)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == adapterId);
            if (nic is null)
            {
                return false;
            }
            return nic.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          && a.Address.ToString() == ip);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Alias: check of {ip} on {adapterId} failed: {ex.Message}");
            return false;
        }
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Cannot start netsh.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (process.ExitCode, output);
    }

    private static string TrimOutput(string output)
    {
        var text = output.Replace("\r", "").Replace("\n", " ").Trim();
        return text.Length > 300 ? text[..300] : text;
    }
}
