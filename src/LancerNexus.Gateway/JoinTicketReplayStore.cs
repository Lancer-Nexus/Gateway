using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LancerNexus.Gateway;

public sealed class JoinTicketReplayStore(IConfiguration configuration)
{
    public async Task<bool> TryConsumeAsync(string nonce, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        var endpoint = configuration["Gateway:RedisEndpoint"];
        if (!TryParseEndpoint(endpoint, out var host, out var port))
            throw new InvalidOperationException("Gateway:RedisEndpoint is not configured.");
        var seconds = Math.Max(1, (int)Math.Ceiling((expiresUtc - DateTime.UtcNow).TotalSeconds));
        var key = $"lancer-nexus:join-ticket:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)))}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var redis = new TcpClient();
        await redis.ConnectAsync(host, port, timeout.Token);
        await using var stream = redis.GetStream();
        var command = $"*6\r\n$3\r\nSET\r\n${key.Length}\r\n{key}\r\n$1\r\n1\r\n$2\r\nNX\r\n$2\r\nEX\r\n${seconds.ToString().Length}\r\n{seconds}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command), timeout.Token);
        var response = new byte[5];
        var read = 0;
        while (read < response.Length)
        {
            var received = await stream.ReadAsync(response.AsMemory(read, response.Length - read), timeout.Token);
            if (received == 0)
                throw new IOException("Redis closed the connection.");
            read += received;
        }
        var result = Encoding.ASCII.GetString(response);
        return result == "+OK\r\n";
    }

    private static bool TryParseEndpoint(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out port)) return false;
        host = value[..separator].Trim();
        return host.Length > 0 && port is > 0 and <= 65535;
    }
}
