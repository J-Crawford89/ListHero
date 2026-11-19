using System.Net;
using System.Net.Sockets;

namespace ListHero.Infrastructure.Products;

public interface IPublicProductNetwork
{
    Uri Validate(string url);
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct);
}

// DNS is checked on the actual connection, not just before handing the hostname to a client.
public sealed class PublicProductNetwork : IPublicProductNetwork
{
    public Uri Validate(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.TrimEnd('.').Contains('.') ||
            uri.IdnHost.TrimEnd('.').EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.TrimEnd('.').EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.TrimEnd('.').EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a public HTTPS product link without a username or password.");
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        _ = Validate($"https://{host}:{port}/");
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip))) throw new HttpRequestException("Destination is unavailable.");
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(ip, port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Destination is unavailable.");
    }
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return IsPublic(ip.MapToIPv4());
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && (b[1] is 0 or 168 || b[1] == 88 && b[2] == 99) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113 ||
                // Azure platform virtual address is reachable from Azure despite looking public.
                ip.Equals(IPAddress.Parse("168.63.129.16")));
        // Only global unicast; exclude documentation, Teredo, 6to4 and special 2001 blocks.
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8)) && !(b[0] == 0x20 && b[1] == 0x02);
    }
}
