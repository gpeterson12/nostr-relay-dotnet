using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NostrRelay.Server.IntegrationTests.TestSupport;

/// <summary>Extension methods for driving a raw <see cref="WebSocket"/> in protocol-level
/// tests: send a JSON text frame, receive one full message (looping until EndOfMessage),
/// or receive until a message of a specific type (index 0) arrives.</summary>
public static class WebSocketTestClient
{
    public static async Task SendAsync(this WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task<JsonElement> ReceiveArrayAsync(this WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement;
    }

    /// <summary>Reads messages until one whose type (index 0) matches <paramref name="type"/>,
    /// skipping others (e.g. a live EVENT that legitimately interleaves before an expected
    /// EOSE, per the register-before-query ordering in NostrConnectionHandler). Bounded by
    /// a timeout so a missing message fails the test instead of hanging forever.</summary>
    public static async Task<JsonElement> ReceiveUntilAsync(this WebSocket socket, string type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await socket.ReceiveArrayAsync(cts.Token);
            if (message[0].GetString() == type)
                return message;
        }
    }
}
