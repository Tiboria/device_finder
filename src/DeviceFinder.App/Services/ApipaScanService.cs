namespace DeviceFinder.App.Services;

/// <summary>
/// Balayage ARP périodique du range APIPA 169.254.0.0/16 via SendARP,
/// parallélisé, **lié à la carte sélectionnée** (l'IP source passée à SendARP
/// force la sortie sur cette carte : aucune résolution via WiFi/VPN).
/// Interrompu proprement au changement de carte et à la fermeture.
/// Un équipement APIPA qui ne répond pas au ping reste détectable par ARP.
/// </summary>
public sealed class ApipaScanService : IDisposable
{
    private const int Parallelism = 128;
    private const int PauseBetweenSweepsSeconds = 45;

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;
    private int _progressPercent = -1;
    private TimeSpan _elapsed;

    /// <summary>(MAC au format AA:BB:CC:DD:EE:FF, IP sondée).</summary>
    public event Action<string, string>? DeviceObservation;

    public bool IsRunning { get; private set; }

    /// <summary>Progression du balayage en cours (0–100), -1 si inactif.</summary>
    public int ProgressPercent => _progressPercent;

    /// <summary>Temps écoulé du balayage en cours (mesure de la durée réelle d'un scan complet).</summary>
    public TimeSpan Elapsed => _elapsed;

    public void Start(AdapterInfo adapter)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        _sweepTask = Task.Run(() => SweepLoopAsync(adapter, token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _sweepTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Annulation en cours : normal.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"APIPA: stop: {ex.Message}");
        }
        _cts?.Dispose();
        _cts = null;
        _sweepTask = null;
        IsRunning = false;
        _progressPercent = -1;
    }

    public void Dispose() => Stop();

    private async Task SweepLoopAsync(AdapterInfo adapter, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            SweepOnce(adapter, token);
            if (token.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PauseBetweenSweepsSeconds), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SweepOnce(AdapterInfo adapter, CancellationToken token)
    {
        long done = 0;
        const long total = 256L * 254;
        var started = DateTime.UtcNow;
        _progressPercent = 0;
        _elapsed = TimeSpan.Zero;
        // Source = IP de la carte sélectionnée : SendARP sort uniquement sur cette carte.
        var sourceIp = adapter.PrimaryIpv4;
        try
        {
            Parallel.For(0, 256,
                new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Parallelism },
                secondOctet =>
                {
                    for (int host = 1; host <= 254; host++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                        var ip = new System.Net.IPAddress(new byte[] { 169, 254, (byte)secondOctet, (byte)host });
                        var mac = SendArpResolver.TryResolve(ip, sourceIp);
                        if (mac is not null)
                        {
                            DeviceObservation?.Invoke(SendArpResolver.FormatMac(mac), ip.ToString());
                        }
                        var count = Interlocked.Increment(ref done);
                        if ((count & 0x1FF) == 0)
                        {
                            _progressPercent = (int)(count * 100 / total);
                            _elapsed = DateTime.UtcNow - started;
                        }
                    }
                });
            _elapsed = DateTime.UtcNow - started;
        }
        catch (OperationCanceledException)
        {
            // Interruption demandée : sortie propre.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"APIPA: sweep interrupted: {ex.Message}");
        }
    }
}
