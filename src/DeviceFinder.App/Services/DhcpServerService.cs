using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DeviceFinder.App.Services;

/// <summary>État d'un bail attribué par le serveur DHCP intégré.</summary>
public enum DhcpLeaseState
{
    /// <summary>Une OFFER a été envoyée, le client n'a pas encore confirmé (DHCPREQUEST).</summary>
    Offered,
    /// <summary>Le client a confirmé (ACK envoyé) : l'adresse est en usage.</summary>
    Bound,
}

/// <summary>Ligne de bail affichée dans l'interface.</summary>
public sealed record DhcpLeaseView(string Mac, string Ip, string State, string Expires);

/// <summary>
/// Mini-serveur DHCP intégré (option activable/désactivable, désactivé par défaut).
/// Le socket écoute sur UDP 67 de toutes les adresses (0.0.0.0) — sur Windows, un
/// socket lié à une IP unicast ne reçoit pas les broadcasts limités
/// (255.255.255.255:67), et WSARecvMsg (ReceiveMessageFromAsync + IP_PKTINFO) est
/// resté muet face à ces mêmes broadcasts sur banc : la réception utilise donc
/// ReceiveFromAsync (recvfrom), primitive prouvée fonctionnelle sur le banc.
/// Le ciblage de la carte sélectionnée repose sur le SCOPAGE des réponses :
/// l'OFFER part en broadcast dirigé vers le /24 du pool (ex. 192.168.99.255),
/// sous-réseau qui n'existe que sur la carte sélectionnée (l'alias du serveur) —
/// un DHCPDISCOVER venu d'une autre carte (WiFi, VPN) ne peut jamais compléter sa
/// transaction : il ne voit pas l'OFFER, qui ne quitte pas le lien direct. À cela
/// s'ajoutent : le contrôle de conflit de sous-réseau à l'activation, l'exclusion
/// des MAC locales, et l'attribution des baux limitée au pool du /24 sélectionné.
/// L'IP du PC sur ce /24 (posée en alias par l'app, tracée avec le marqueur
/// <see cref="AliasIpService.DhcpMarkerMac"/> et donc nettoyée comme toute IP
/// alias) sert d'adresse source des réponses, de passerelle et de « server
/// identifier ». La transaction DHCP (OFFER puis ACK) révèle directement la MAC
/// du client et l'IP attribuée : le couple est remonté via
/// <see cref="DeviceObservation"/> pour nourrir la liste des devices.
/// </summary>
public sealed class DhcpServerService : IDisposable
{
    private const int DhcpServerPort = 67;
    private const int DhcpClientPort = 68;
    /// <summary>Durée de bail minimale acceptée (plancher : 5 minutes).</summary>
    public const int MinLeaseSeconds = 300;
    /// <summary>Une OFFER non confirmée par un DHCPREQUEST est libérée après ce délai.</summary>
    private const int OfferHoldSeconds = 60;
    public const int MaxPoolSize = 128;
    private sealed class DhcpPacket
    {
        public required byte[] Raw { get; init; }
        public required byte HType { get; init; }
        public required byte HLen { get; init; }
        public required string Mac { get; init; }
        public required IPAddress ClientAddress { get; init; }   // ciaddr
        public required IPAddress OfferedAddress { get; init; }  // yiaddr (reçu : sans objet)
        public required IPAddress RelayAddress { get; init; }    // giaddr
        /// <summary>Option 53 : 1 = DISCOVER, 3 = REQUEST, 4 = DECLINE, 7 = RELEASE.</summary>
        public byte MessageType { get; set; }
        /// <summary>Option 50 : IP demandée par le client.</summary>
        public IPAddress? RequestedIp { get; set; }
        /// <summary>Option 54 : serveur choisi par le client (SELECTING).</summary>
        public IPAddress? ServerId { get; set; }
    }

    private sealed class DhcpLease
    {
        public required string Mac { get; init; }
        public required IPAddress Ip { get; init; }
        public DhcpLeaseState State { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, DhcpLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private IPAddress? _serverIp;
    private IPAddress? _poolStart;
    private IPAddress? _poolEnd;
    private IPAddress? _poolBroadcast;

    /// <summary>Durée du bail attribué (option 51), clampée au plancher <see cref="MinLeaseSeconds"/>.</summary>
    private int _leaseSeconds = 600;

    /// <summary>
    /// Baux attribués conservés après l'arrêt du serveur, pour information :
    /// un bail survit à la fermeture du serveur (l'équipement garde son IP jusqu'à
    /// expiration) — la table reste donc lisible, marquée « serveur arrêté ».
    /// </summary>
    private List<DhcpLease> _stoppedLeases = new();

    private long _discoversSeen;
    private long _offersSent;
    private long _requestsSeen;
    private long _acksSent;
    private long _naksSent;
    private long _datagramsSeen;
    private long _receiveErrors;
    private long _ignoredOtherServer;
    private long _nonDhcp;
    private long _unknownType;
    private long _rejectedLocalMac;
    private long _rejectedRelay;
    private long _unconfirmedOffers;
    private long _releasedOrDeclined;

    /// <summary>(MAC au format AA:BB:CC:DD:EE:FF, IP attribuée) — levée à l'OFFER puis à l'ACK.</summary>
    public event Action<string, string>? DeviceObservation;

    /// <summary>Levée quand la liste des baux change (ajout, ACK, expiration).</summary>
    public event Action? LeasesChanged;

    /// <summary>Messages d'état (transactions DHCP, pool épuisé…).</summary>
    public event Action<string>? StatusMessage;

    private long _lastActivityTicks;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public string? AdapterName { get; private set; }
    public string? ServerIpText { get; private set; }
    public string? PoolStartText { get; private set; }
    public string? PoolEndText { get; private set; }

    /// <summary>Durée du bail réellement attribuée (option 51), en secondes.</summary>
    public int LeaseSeconds => _leaseSeconds;

    /// <summary>
    /// Dernière activité DHCP : datagramme valide reçu ou réponse émise (UTC).
    /// Null si aucune activité depuis le démarrage du serveur.
    /// </summary>
    public DateTime? LastActivityUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastActivityTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private void TouchActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public long DiscoversSeen => Interlocked.Read(ref _discoversSeen);
    public long OffersSent => Interlocked.Read(ref _offersSent);
    public long RequestsSeen => Interlocked.Read(ref _requestsSeen);
    public long AcksSent => Interlocked.Read(ref _acksSent);
    public long NaksSent => Interlocked.Read(ref _naksSent);
    /// <summary>Datagrammes UDP reçus sur le port 67 — incrémenté AVANT tout traitement.</summary>
    public long DatagramsSeen => Interlocked.Read(ref _datagramsSeen);
    /// <summary>Erreurs de réception/traitement : aucune exception n'est avalée.</summary>
    public long ReceiveErrors => Interlocked.Read(ref _receiveErrors);
    public string? LastReceiveError { get; private set; }
    public long IgnoredOtherServer => Interlocked.Read(ref _ignoredOtherServer);
    /// <summary>Datagrammes rejetés au parse (trop courts, pas BOOTP, cookie absent…) — jamais silencieux : diagnostic hex conservé.</summary>
    public long NonDhcp => Interlocked.Read(ref _nonDhcp);
    /// <summary>Dernier diagnostic d'un datagramme non reconnu (longueur + premiers octets en hex).</summary>
    public string? LastIgnoredDiagnostic { get; private set; }
    /// <summary>Messages DHCP valides mais non traités (INFORM, etc.).</summary>
    public long UnknownMessageType => Interlocked.Read(ref _unknownType);
    /// <summary>Datagrammes rejetés : MAC de la machine locale.</summary>
    public long RejectedLocalMac => Interlocked.Read(ref _rejectedLocalMac);
    /// <summary>Datagrammes rejetés : client derrière un relais (giaddr non nul).</summary>
    public long RejectedRelay => Interlocked.Read(ref _rejectedRelay);
    /// <summary>
    /// OFFER envoyées jamais confirmées par un REQUEST (expirées) : c'est la signature
    /// mesurable d'un DISCOVER venu d'une autre carte (WiFi) — l'OFFER, émise en
    /// broadcast dirigé du /24 du pool, ne quitte pas la carte sélectionnée, le client
    /// hors carte ne la voit pas et ne peut pas confirmer.
    /// </summary>
    public long UnconfirmedOffers => Interlocked.Read(ref _unconfirmedOffers);
    /// <summary>RELEASE/DECLINE reçus (avec ou sans bail correspondant).</summary>
    public long ReleasedOrDeclined => Interlocked.Read(ref _releasedOrDeclined);

    /// <summary>Bails actuellement actifs (OFFER non confirmée exclue après purge).</summary>
    public int BoundLeases
    {
        get
        {
            lock (_gate)
            {
                PurgeExpiredUnsafe();
                return _leases.Values.Count(l => l.State == DhcpLeaseState.Bound);
            }
        }
    }

    /// <summary>
    /// Démarre le serveur sur la carte donnée. <paramref name="serverIp"/> doit déjà être
    /// présente sur la carte (l'appelant la pose en alias si nécessaire) : c'est elle qui
    /// sert d'adresse source, de passerelle et de « server identifier ».
    /// <paramref name="leaseSeconds"/> est la durée du bail transmise dans l'option 51
    /// des OFFER et ACK ; elle est clampée au plancher <see cref="MinLeaseSeconds"/>
    /// (5 minutes) : un bail survit à la fermeture du serveur, plus court que ça il ne
    /// tiendrait même pas le temps de lire sa page web.
    /// </summary>
    public bool Start(AdapterInfo adapter, IPAddress serverIp, IPAddress poolStart, IPAddress poolEnd, int leaseSeconds)
    {
        Stop();
        _leaseSeconds = Math.Max(MinLeaseSeconds, leaseSeconds);
        _lastActivityTicks = 0;
        if (!IsValidPool(serverIp, poolStart, poolEnd, out var error))
        {
            LastError = error;
            return false;
        }

        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Pas de SO_REUSEADDR : le port 67 doit être exclusif. Si un autre
            // serveur DHCP l'accapare (service DHCP Windows, partage de connexion
            // /ICS), le bind échoue et l'utilisateur est prévenu au lieu que les
            // deux serveurs répondent en concurrence.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            // Bind sur 0.0.0.0 : sur Windows, un socket lié à une IP unicast ne
            // reçoit PAS les broadcasts limités (255.255.255.255:67) des DHCPDISCOVER.
            // Pas d'IP_PKTINFO : sur Windows, WSARecvMsg (ReceiveMessageFromAsync)
            // est resté muet face aux broadcasts limités sur le banc — la réception
            // utilise ReceiveFromAsync (voir StartReceiveLoop).
            socket.Bind(new IPEndPoint(IPAddress.Any, DhcpServerPort));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            LastError = Loc.Fmt("Dhcp.PortInUse", DhcpServerPort);
            return false;
        }
        catch (Exception ex)
        {
            LastError = Loc.Fmt("Dhcp.PortUnavailable", DhcpServerPort, ex.Message);
            return false;
        }

        var poolBytes = poolStart.GetAddressBytes();
        _serverIp = serverIp;
        _poolStart = poolStart;
        _poolEnd = poolEnd;
        _poolBroadcast = new IPAddress(new byte[] { poolBytes[0], poolBytes[1], poolBytes[2], 255 });
        AdapterName = adapter.Name;
        ServerIpText = serverIp.ToString();
        PoolStartText = poolStart.ToString();
        PoolEndText = poolEnd.ToString();

        _cts = new CancellationTokenSource();
        // Auditeur : une seule instance de socket — celle-ci est bindée ci-dessus,
        // reçue par StartReceiveLoop (même objet) et exposée dans _socket pour les
        // émissions (SendTo). Jamais de seconde socket parallèle.
        _socket = socket;
        StartReceiveLoop(socket, _cts.Token);
        IsRunning = true;
        LastError = null;
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _socket?.Close();
        }
        catch
        {
            // déjà fermée
        }
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        bool hadLeases;
        lock (_gate)
        {
            hadLeases = _leases.Count > 0;
            foreach (var lease in _leases.Values.Where(l => l.State == DhcpLeaseState.Offered))
            {
                // OFFER en attente à l'arrêt du serveur : jamais confirmées — comptées.
                Interlocked.Increment(ref _unconfirmedOffers);
            }
            // Les baux attribués restent en table pour information après l'arrêt :
            // l'équipement conserve son IP jusqu'à expiration du bail. Une table de
            // baux vide n'écrase pas le souvenir d'un arrêt précédent.
            var bound = _leases.Values.Where(l => l.State == DhcpLeaseState.Bound).ToList();
            if (bound.Count > 0)
            {
                _stoppedLeases = bound;
            }
            _leases.Clear();
        }
        if (hadLeases)
        {
            LeasesChanged?.Invoke();
        }
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- Pare-feu (UDP 67 entrant)

    public sealed record FirewallRuleResult(bool Ok, string Message);

    // Nom de règle pare-feu NEUTRE EN LANGUE (identique fr/en) : la règle est
    // recréée à chaque activation, un nom qui changerait avec la langue laisserait
    // des doublons d'une langue à l'autre.
    private const string FirewallRuleName = "Device Finder DHCP (UDP 67)";

    /// <summary>Noms historiques de la règle, supprimés à chaque activation (migrations).</summary>
    private static readonly string[] LegacyFirewallRuleNames =
    {
        "Device Finder — DHCP entrant UDP 67",
    };

    /// <summary>
    /// Garantit la règle pare-feu autorisant l'UDP 67 entrant pour CET exécutable :
    /// sans elle, le pare-feu Windows jette les DHCPDISCOVER silencieusement (le bind
    /// réussit, aucune trame n'arrive jamais — échec muet). La règle est recréée à
    /// chaque activation (delete puis add) pour suivre le chemin courant de
    /// l'exécutable (usage portable sur clé USB) ; le profil « any » couvre le cas
    /// du banc direct classé « Réseau public ». Retourne un message de guidance avec
    /// la commande manuelle si la création est impossible.
    /// </summary>
    public static FirewallRuleResult EnsureInboundUdp67Rule()
    {
        // Retire l'éventuelle règle d'une session précédente (chemin d'exécutable
        // périmé, ancien nom de règle d'une version précédente) : l'ajout ci-dessous
        // la recrée à l'identique, sans accumulation.
        foreach (var name in LegacyFirewallRuleNames.Prepend(FirewallRuleName))
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
        }

        var program = Environment.ProcessPath;
        var add = "advfirewall firewall add rule " +
            $"name=\"{FirewallRuleName}\" dir=in action=allow protocol=UDP localport=67 profile=any";
        if (!string.IsNullOrEmpty(program))
        {
            add += $" program=\"{program}\"";
        }
        var (code, output) = RunNetsh(add);
        if (code == 0)
        {
            return new FirewallRuleResult(true, Loc.Get("Dhcp.FirewallOk"));
        }
        return new FirewallRuleResult(false,
            Loc.Fmt("Dhcp.FirewallManual", TrimOutput(output)));
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

    /// <summary>
    /// Baux affichés, triés par IP. Serveur actif : baux vivants (purge des expirés).
    /// Serveur arrêté : derniers baux attribués conservés pour information, marqués
    /// « serveur arrêté » — le bail survit à l'arrêt du serveur côté équipement.
    /// </summary>
    public IReadOnlyList<DhcpLeaseView> GetLeaseViews()
    {
        lock (_gate)
        {
            if (!IsRunning)
            {
                return _stoppedLeases
                    .OrderBy(l => NetworkAdapterService.ToUint(l.Ip))
                    .Select(l => new DhcpLeaseView(
                        l.Mac,
                        l.Ip.ToString(),
                        Loc.Get("Dhcp.LeaseBoundStopped"),
                        l.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss")))
                    .ToList();
            }
            PurgeExpiredUnsafe();
            return _leases.Values
                .OrderBy(l => NetworkAdapterService.ToUint(l.Ip))
                .Select(l => new DhcpLeaseView(
                    l.Mac,
                    l.Ip.ToString(),
                    l.State == DhcpLeaseState.Bound ? Loc.Get("Dhcp.LeaseBound") : Loc.Get("Dhcp.LeaseOffered"),
                    l.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss")))
                .ToList();
        }
    }

    /// <summary>
    /// Valide la configuration du pool : même /24 pour le pool et l'IP du serveur,
    /// début ≤ fin, taille raisonnable, adresse réseau/broadcast exclues, IP du PC
    /// hors du pool, aucune IP locale du PC dans le pool.
    /// </summary>
    public static bool IsValidPool(IPAddress serverIp, IPAddress start, IPAddress end, out string? error)
    {
        error = null;
        var s = start.GetAddressBytes();
        var e = end.GetAddressBytes();
        var sv = serverIp.GetAddressBytes();
        if (s[3] is 0 or 255 || e[3] is 0 or 255 || sv[3] is 0 or 255)
        {
            error = Loc.Get("Dhcp.ReservedAddresses");
            return false;
        }
        if (s[0] != e[0] || s[1] != e[1] || s[2] != e[2]
            || s[0] != sv[0] || s[1] != sv[1] || s[2] != sv[2])
        {
            error = Loc.Get("Dhcp.NotSameSubnet");
            return false;
        }
        uint su = NetworkAdapterService.ToUint(start);
        uint eu = NetworkAdapterService.ToUint(end);
        uint vu = NetworkAdapterService.ToUint(serverIp);
        if (su > eu)
        {
            error = Loc.Get("Dhcp.StartAfterEnd");
            return false;
        }
        long size = eu - su + 1;
        if (size > MaxPoolSize)
        {
            error = Loc.Fmt("Dhcp.PoolTooLarge", size, MaxPoolSize);
            return false;
        }
        if (vu >= su && vu <= eu)
        {
            error = Loc.Fmt("Dhcp.ServerIpInsidePool", serverIp, start, end);
            return false;
        }
        foreach (var poolIp in PoolAddresses(start, end))
        {
            if (LocalHostFilter.IsLocalIp(poolIp.ToString()))
            {
                error = Loc.Fmt("Dhcp.PoolContainsLocalIp", poolIp);
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- Réception

    /// <summary>
    /// Boucle de réception du serveur. Reçoit sur LE MÊME objet socket que celui qui
    /// a été bindé (passé en paramètre depuis Start). La réception utilise
    /// ReceiveFromAsync (recvfrom simple) : sur le banc du 2e correctif, cette
    /// primitive recevait les DISCOVER en broadcast limité (255.255.255.255:67)
    /// alors que ReceiveMessageFromAsync (WSARecvMsg + IP_PKTINFO) restait muette.
    /// IP_PKTINFO est donc absent : le ciblage de carte repose sur le scoping des
    /// réponses (voir la doc de la classe).
    /// Chaque itération est blindée : comptage brut AVANT tout traitement, aucune
    /// exception avalée (compteur « erreurs de réception » + message de statut).
    /// </summary>
    private void StartReceiveLoop(Socket socket, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(
                        new ArraySegment<byte>(buffer), SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0));
                    // Comptage brut, avant tout traitement : ce compteur doit
                    // bouger dès le premier datagramme, quel qu'il soit.
                    Interlocked.Increment(ref _datagramsSeen);
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Aucune exception avalée : compteur + message visibles en statut.
                    Interlocked.Increment(ref _receiveErrors);
                    LastReceiveError = ex.Message;
                    StatusMessage?.Invoke(
                        Loc.Fmt("Dhcp.RecvError", _receiveErrors, ex.Message));
                    continue;
                }

                try
                {
                    var data = new byte[result.ReceivedBytes];
                    Array.Copy(buffer, data, result.ReceivedBytes);
                    HandleDatagram(data);
                }
                catch (Exception ex)
                {
                    // Le traitement d'un datagramme ne doit jamais tuer la boucle.
                    Interlocked.Increment(ref _receiveErrors);
                    LastReceiveError = ex.Message;
                    StatusMessage?.Invoke(
                        Loc.Fmt("Dhcp.HandleError", ex.Message));
                }
            }
        }, token);
    }

    /// <summary>
    /// Tri d'un datagramme reçu sur le port 67. Chaque sortie est comptée et visible :
    /// aucun chemin muet entre la réception brute et l'OFFER. Les compteurs DISCOVER/
    /// REQUEST s'incrémentent dès l'identification du type, AVANT les filtres, pour
    /// que la séquence de l'acceptation (datagrammes → DISCOVER → OFFER → REQUEST →
    /// ACK) soit lisible sans trou quel que soit le rejet ensuite appliqué.
    /// Nota : aucun filtre d'arrivée par IP locale ou index de carte n'existe ici —
    /// le ciblage de la carte sélectionnée repose sur le scoping des réponses (cf.
    /// doc de la classe) ; l'absence de confirmation comptée par UnconfirmedOffers
    /// est la signature d'un client hors carte.
    /// </summary>
    private void HandleDatagram(byte[] data)
    {
        var packet = TryParse(data, out var reason);
        if (packet is null)
        {
            Interlocked.Increment(ref _nonDhcp);
            var diag = Loc.Fmt("Dhcp.DiagSuffix", reason, FirstBytesHex(data));
            LastIgnoredDiagnostic = diag;
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.NonDhcpIgnored", _nonDhcp, diag));
            return;
        }

        // Datagramme DHCP valide reçu : c'est une « activité » serveur (barre compacte).
        TouchActivity();

        // Comptage par type AVANT tout filtre : « DISCOVER reçus » doit refléter
        // la réception, indépendamment des rejets qui suivent.
        switch (packet.MessageType)
        {
            case 1:
                Interlocked.Increment(ref _discoversSeen);
                break;
            case 3:
                Interlocked.Increment(ref _requestsSeen);
                break;
        }

        if (packet.MessageType is not ((byte)1 or 3 or 4 or 7))
        {
            Interlocked.Increment(ref _unknownType);
            // Type 0 = option 53 introuvable : le cookie est là mais la zone
            // options n'a pas livré le type — le diagnostic hex tranche sur le banc.
            string hint = packet.MessageType == 0
                ? Loc.Get("Dhcp.HintOption53Missing")
                : Loc.Get("Dhcp.HintUnhandledType");
            StatusMessage?.Invoke(
                Loc.Fmt("Dhcp.UnknownType", packet.MessageType, hint, FirstBytesHex(data)));
            return;
        }
        if (!packet.RelayAddress.Equals(IPAddress.Any))
        {
            // Câble direct uniquement : jamais de client derrière un relais DHCP.
            Interlocked.Increment(ref _rejectedRelay);
            StatusMessage?.Invoke(
                Loc.Fmt("Dhcp.RelayRejected", packet.Mac, packet.RelayAddress));
            return;
        }
        if (LocalHostFilter.IsLocalMac(packet.Mac))
        {
            Interlocked.Increment(ref _rejectedLocalMac);
            return;
        }

        switch (packet.MessageType)
        {
            case 1: // DHCPDISCOVER
                HandleDiscover(packet);
                break;
            case 3: // DHCPREQUEST
                HandleRequest(packet);
                break;
            case 4: // DHCPDECLINE : le client a détecté un conflit sur l'offre
                HandleReleaseLike(packet, "DECLINE");
                break;
            case 7: // DHCPRELEASE : le client libère son bail
                HandleReleaseLike(packet, "RELEASE");
                break;
        }
    }

    private void HandleDiscover(DhcpPacket packet)
    {
        var ip = AllocateIp(packet);
        if (ip is null)
        {
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.PoolExhausted", packet.Mac, _poolStart, _poolEnd));
            return;
        }
        Interlocked.Increment(ref _offersSent);
        SendReply(packet, messageType: 2, yiaddr: ip);
        DeviceObservation?.Invoke(packet.Mac, ip.ToString());
        StatusMessage?.Invoke(Loc.Fmt("Dhcp.OfferSent", ip, packet.Mac));
        LeasesChanged?.Invoke();
    }

    private void HandleRequest(DhcpPacket packet)
    {
        // Sélection pour un autre serveur DHCP : ne pas interférer.
        if (packet.ServerId is not null && !packet.ServerId.Equals(_serverIp))
        {
            Interlocked.Increment(ref _ignoredOtherServer);
            return;
        }
        var requested = packet.RequestedIp
            ?? (!packet.ClientAddress.Equals(IPAddress.Any) ? packet.ClientAddress : null);
        DhcpLease? lease;
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            _leases.TryGetValue(packet.Mac, out lease);
        }
        if (lease is not null && requested is not null && lease.Ip.Equals(requested))
        {
            // Confirmation d'une OFFER ou renouvellement : ACK.
            lock (_gate)
            {
                lease.State = DhcpLeaseState.Bound;
                lease.ExpiresUtc = DateTime.UtcNow.AddSeconds(_leaseSeconds);
            }
            Interlocked.Increment(ref _acksSent);
            SendReply(packet, messageType: 5, yiaddr: lease.Ip);
            DeviceObservation?.Invoke(packet.Mac, lease.Ip.ToString());
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.AckSent", lease.Ip, packet.Mac));
            LeasesChanged?.Invoke();
        }
        else
        {
            // Demande sans offre correspondante (bail expiré, autre serveur) :
            // NAK pour que le client reparte sur un DHCPDISCOVER propre.
            lock (_gate)
            {
                if (lease is not null)
                {
                    _leases.Remove(packet.Mac);
                }
            }
            Interlocked.Increment(ref _naksSent);
            SendReply(packet, messageType: 6, yiaddr: IPAddress.Any);
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.NakSent", packet.Mac));
            LeasesChanged?.Invoke();
        }
    }

    private void HandleReleaseLike(DhcpPacket packet, string kind)
    {
        Interlocked.Increment(ref _releasedOrDeclined);
        bool removed;
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            removed = _leases.Remove(packet.Mac);
        }
        if (removed)
        {
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.LeaseReleased", kind, packet.Mac));
            LeasesChanged?.Invoke();
        }
        else
        {
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.ReleaseNoLease", kind, packet.Mac));
        }
    }

    // ---------------------------------------------------------------- Allocation

    /// <summary>
    /// Choisis l'adresse à offrir : bail existant réutilisé tel quel (OFFER non
    /// confirmée, re-DISCOVER ou renouvellement — le client détient déjà l'adresse,
    /// aucun contrôle de conflit : c'est lui qui répondrait à l'ARP), sinon IP
    /// demandée par le client si elle est libre, sinon première adresse libre du
    /// pool. « Libre » = pas déjà allouée à une autre MAC, pas une IP locale et
    /// personne ne répond à un SendARP depuis l'IP du serveur.
    /// </summary>
    private IPAddress? AllocateIp(DhcpPacket packet)
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            if (_leases.TryGetValue(packet.Mac, out var existing))
            {
                existing.ExpiresUtc = DateTime.UtcNow.AddSeconds(OfferHoldSeconds);
                return existing.Ip;
            }
        }

        // Allocation nouvelle : honorer l'IP demandée (option 50) si elle est libre.
        if (packet.RequestedIp is not null && IsInPool(packet.RequestedIp)
            && !IsLeased(packet.RequestedIp) && IsFreeOnLink(packet.RequestedIp))
        {
            CommitOffer(packet.Mac, packet.RequestedIp);
            return packet.RequestedIp;
        }

        // Sinon : balayer le pool et prendre la première adresse réellement libre.
        foreach (var ip in PoolAddresses(_poolStart!, _poolEnd!))
        {
            if (IsLeased(ip))
            {
                continue;
            }
            if (!IsFreeOnLink(ip))
            {
                continue;
            }
            CommitOffer(packet.Mac, ip);
            return ip;
        }
        return null;
    }

    /// <summary>
    /// Adresses du pool dans l'ordre (début → fin) : la validation garantit un même
    /// /24, seul le dernier octet varie. NB : ne pas utiliser new IPAddress(uint) —
    /// ce constructeur interprète la valeur avec des octets inversés par rapport à
    /// l'ordre réseau.
    /// </summary>
    private static IEnumerable<IPAddress> PoolAddresses(IPAddress start, IPAddress end)
    {
        var p = start.GetAddressBytes();
        byte lastHost = end.GetAddressBytes()[3];
        for (byte host = p[3]; ; host++)
        {
            yield return new IPAddress(new byte[] { p[0], p[1], p[2], host });
            if (host == lastHost)
            {
                yield break;
            }
        }
    }

    private bool IsLeased(IPAddress ip)
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return IsLeasedUnsafe(ip);
        }
    }

    private void CommitOffer(string mac, IPAddress ip)
    {
        lock (_gate)
        {
            _leases[mac] = new DhcpLease
            {
                Mac = mac,
                Ip = ip,
                State = DhcpLeaseState.Offered,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(OfferHoldSeconds),
            };
        }
    }

    private bool IsInPool(IPAddress ip)
    {
        uint u = NetworkAdapterService.ToUint(ip);
        return u >= NetworkAdapterService.ToUint(_poolStart!) && u <= NetworkAdapterService.ToUint(_poolEnd!);
    }

    private bool IsLeasedUnsafe(IPAddress ip) => _leases.Values.Any(l => l.Ip.Equals(ip));

    /// <summary>Personne d'autre ne possède l'adresse : pas locale + aucun répondant ARP.</summary>
    private bool IsFreeOnLink(IPAddress ip)
    {
        var text = ip.ToString();
        if (LocalHostFilter.IsLocalIp(text) || ip.Equals(_serverIp))
        {
            return false;
        }
        return SendArpResolver.TryResolve(ip, _serverIp) is null;
    }

    private void PurgeExpiredUnsafe()
    {
        var now = DateTime.UtcNow;
        var expired = _leases.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList();
        foreach (var mac in expired)
        {
            // Une OFFER jamais confirmée est la signature mesurable d'un client
            // qu la transaction n'a jamais pu atteindre (hors carte) — comptée.
            if (_leases[mac].State == DhcpLeaseState.Offered)
            {
                Interlocked.Increment(ref _unconfirmedOffers);
            }
            _leases.Remove(mac);
        }
    }

    // ---------------------------------------------------------------- Émission

    /// <summary>
    /// Construit et envoie la réponse (OFFER/ACK/NAK) en broadcast dirigé vers le /24
    /// du pool : le datagramme sort par la carte qui porte le sous-réseau (l'alias du
    /// serveur), jamais par la route par défaut.
    /// </summary>
    private void SendReply(DhcpPacket request, byte messageType, IPAddress yiaddr)
    {
        var socket = _socket;
        if (socket is null || !IsRunning)
        {
            return;
        }
        var reply = new byte[300];
        reply[0] = 2;                     // BOOTREPLY
        reply[1] = request.HType;
        reply[2] = request.HLen;
        Array.Copy(request.Raw, 4, reply, 4, 4);   // xid
        Array.Copy(request.Raw, 10, reply, 10, 2); // flags
        CopyAddress(yiaddr, reply, 16);            // yiaddr
        CopyAddress(_serverIp!, reply, 20);        // siaddr = serveur
        CopyAddress(request.RelayAddress, reply, 24);
        Array.Copy(request.Raw, 28, reply, 28, 16); // chaddr
        reply[236] = 0x63;
        reply[237] = 0x82;
        reply[238] = 0x53;
        reply[239] = 0x63;

        var options = new List<byte>();
        AppendOption(options, 53, messageType);
        AppendOption(options, 54, _serverIp!.GetAddressBytes()); // server identifier
        if (messageType is 2 or 5) // OFFER / ACK : paramètres complets
        {
            AppendOption(options, 51, ToNetworkBytes((uint)_leaseSeconds)); // durée du bail
            AppendOption(options, 1, new byte[] { 255, 255, 255, 0 });     // masque /24
            AppendOption(options, 3, _serverIp.GetAddressBytes());         // passerelle = IP du PC
            AppendOption(options, 58, ToNetworkBytes((uint)(_leaseSeconds / 2)));       // T1
            AppendOption(options, 59, ToNetworkBytes((uint)(_leaseSeconds * 7 / 8)));  // T2
            AppendOption(options, 28, _poolBroadcast!.GetAddressBytes());  // broadcast du /24
        }
        options.Add(255); // fin des options
        var used = Math.Min(options.Count, reply.Length - 240);
        for (int i = 0; i < used; i++)
        {
            reply[240 + i] = options[i];
        }

        try
        {
            socket.SendTo(reply, new IPEndPoint(_poolBroadcast!, DhcpClientPort));
            TouchActivity();
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(Loc.Fmt("Dhcp.SendFailed", ex.Message));
        }
    }

    private static void CopyAddress(IPAddress address, byte[] target, int offset)
    {
        var bytes = address.GetAddressBytes();
        target[offset] = bytes[0];
        target[offset + 1] = bytes[1];
        target[offset + 2] = bytes[2];
        target[offset + 3] = bytes[3];
    }

    private static void AppendOption(List<byte> options, byte type, params byte[] data)
    {
        options.Add(type);
        options.Add((byte)data.Length);
        options.AddRange(data);
    }

    private static byte[] ToNetworkBytes(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes); // big-endian
        return bytes;
    }

    /// <summary>
    /// Diagnostic d'un datagramme non reconnu : longueur, 32 premiers octets
    /// (op/htype/hlen/xid/ciaddr… chaddr) et la zone cookie+options à partir de
    /// l'offset 236 — c'est là que se jouent la validation et le tri.
    /// </summary>
    private static string FirstBytesHex(byte[] data)
    {
        int head = Math.Min(32, data.Length);
        var sb = new System.Text.StringBuilder();
        sb.Append(data.Length).Append(Loc.Get("Dhcp.DiagBytes")).Append(' ')
          .Append(BitConverter.ToString(data, 0, head));
        if (data.Length > 236)
        {
            sb.Append(" · ").Append(Loc.Get("Dhcp.DiagCookie")).Append('=')
              .Append(BitConverter.ToString(data, 236, Math.Min(24, data.Length - 236)));
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Analyse des paquets

    /// <summary>
    /// Parse un datagramme BOOTP/DHCP (op=1, htype=1, hlen=6, cookie 0x63825363 à
    /// l'offset 236, options à partir de 240). Ne lève JAMAIS : tout datagramme
    /// malformé est rejeté proprement avec un motif (<paramref name="reason"/>)
    /// que HandleDatagram compte et affiche.
    /// </summary>
    private static DhcpPacket? TryParse(byte[] data, out string? reason)
    {
        reason = null;
        if (data.Length < 240)
        {
            reason = Loc.Fmt("Dhcp.RejectTooShort", data.Length);
            return null;
        }
        // Paquet BOOTREQUEST IPv4/Ethernet uniquement.
        if (data[0] != 1 || data[1] != 1 || data[2] != 6)
        {
            reason = Loc.Fmt("Dhcp.RejectNotBootp", data[0], data[1], data[2]);
            return null;
        }
        if (data[236] != 0x63 || data[237] != 0x82 || data[238] != 0x53 || data[239] != 0x63)
        {
            reason = Loc.Fmt("Dhcp.RejectCookie", data[236], data[237], data[238], data[239]);
            return null;
        }
        var mac = string.Join(":", Enumerable.Range(0, 6).Select(i => data[28 + i].ToString("X2")));
        if (mac == "00:00:00:00:00:00")
        {
            reason = Loc.Get("Dhcp.RejectNullChaddr");
            return null;
        }
        var packet = new DhcpPacket
        {
            Raw = data,
            HType = data[1],
            HLen = data[2],
            Mac = mac,
            ClientAddress = new IPAddress(data[12..16]),  // ciaddr
            OfferedAddress = new IPAddress(data[16..20]), // yiaddr
            RelayAddress = new IPAddress(data[24..28]),   // giaddr
        };

        int i = 240;
        while (i < data.Length)
        {
            var option = data[i];
            if (option == 0)
            {
                i++;
                continue;
            }
            if (option == 255)
            {
                break;
            }
            if (i + 1 >= data.Length)
            {
                break;
            }
            var length = data[i + 1];
            if (i + 2 + length > data.Length)
            {
                break;
            }
            switch (option)
            {
                case 53 when length >= 1:
                    packet.MessageType = data[i + 2];
                    break;
                case 50 when length == 4:
                    packet.RequestedIp = new IPAddress(data[(i + 2)..(i + 6)]);
                    break;
                case 54 when length == 4:
                    packet.ServerId = new IPAddress(data[(i + 2)..(i + 6)]);
                    break;
            }
            i += 2 + length;
        }
        reason = null;
        return packet;
    }
}
