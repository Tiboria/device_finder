using System.Net.NetworkInformation;
using DeviceFinder.App.Models;

namespace DeviceFinder.App.Services;

/// <summary>
/// Pastille d'état « joignable » : ping ICMP léger toutes les 5 secondes
/// pour chaque device listé. Simple indicateur, pas un module de monitoring.
/// </summary>
public sealed class ReachabilityMonitor : IDisposable
{
    private readonly DeviceStore _store;
    private CancellationTokenSource? _cts;

    public ReachabilityMonitor(DeviceStore store)
    {
        _store = store;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    public static async Task<bool> PingAsync(string ip, int timeoutMs = 900)
    {
        try
        {
            if (!System.Net.IPAddress.TryParse(ip, out var address))
            {
                return false;
            }
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var devices = _store.GetAll();
            var pings = devices
                .Where(d => d.CurrentIp is not null)
                .Select(async d => d.SetReachability(await PingAsync(d.CurrentIp!)));
            try
            {
                await Task.WhenAll(pings);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
