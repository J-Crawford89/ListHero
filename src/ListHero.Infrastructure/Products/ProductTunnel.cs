using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ListHero.Infrastructure.Products;

// CONNECT-only, loopback-only and ephemeral. Chromium's own TLS crosses the tunnel, while
// DNS/IP validation and the selected socket remain under our control. No TLS interception.
public sealed class ProductTunnel : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime;
    private readonly Task accept;
    private readonly List<Task> clients = [];
    public string Address => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    public ProductTunnel(IPublicProductNetwork network, CancellationToken ct)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); listener.Start(4);
        accept = AcceptAsync(network);
    }
    private async Task AcceptAsync(IPublicProductNetwork network)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                // One document and a handful of redirects; never an unbounded proxy pool.
                if (clients.Count >= 12) { client.Dispose(); continue; }
                clients.Add(HandleAsync(client, network));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client, IPublicProductNetwork network)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
            try
            {
                await using var incoming = client.GetStream();
                var header = new List<byte>(); var single = new byte[1];
                while (header.Count < 8192 && await incoming.ReadAsync(single, ct) != 0)
                {
                    header.Add(single[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0].Split(' ');
                if (line.Length != 3 || line[0] != "CONNECT" || !line[1].EndsWith(":443", StringComparison.Ordinal)) return;
                var uri = network.Validate("https://" + line[1] + "/");
                await using var outgoing = await network.ConnectAsync(uri.IdnHost, uri.Port, ct);
                await incoming.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                // Bound traffic even when a retailer streams an unexpectedly large response.
                var upload = CopyAsync(incoming, outgoing, 256 * 1024, ct);
                var download = CopyAsync(outgoing, incoming, 6 * 1024 * 1024, ct);
                await Task.WhenAny(upload, download); deadline.Cancel();
                try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or HttpRequestException or OperationCanceledException or ArgumentException) { }
        }
    }
    private static async Task CopyAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if ((limit -= count) < 0) throw new IOException("Transfer limit exceeded.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); listener.Stop(); await accept;
        await Task.WhenAll(clients); lifetime.Dispose();
    }
}
