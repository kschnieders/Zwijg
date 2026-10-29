using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Zwijg.Gateway.Providers;

namespace Zwijg.Tests;

// Prüft, was wirklich an Claude geht. Ein lokaler Server spielt die API.
public class AnthropicProviderTests
{
    private const string Reply = """
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"Hallo zurück"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":3}}
        """;

    [Fact]
    public async Task Anfrage_hat_Rollen_Version_und_Schluessel_richtig()
    {
        // Freien Port suchen und gleich wieder freigeben
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();

        string? body = null, version = null, auth = null;
        var serve = Task.Run(async () =>
        {
            var ctx = await server.GetContextAsync();
            using (var reader = new StreamReader(ctx.Request.InputStream))
                body = await reader.ReadToEndAsync();
            version = ctx.Request.Headers["anthropic-version"];
            auth = ctx.Request.Headers["Authorization"];
            var bytes = Encoding.UTF8.GetBytes(Reply);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", $"http://127.0.0.1:{port}");
        try
        {
            var provider = new AnthropicProvider("test-schluessel", "claude-opus-5-5", "medium", 30);
            var result = await provider.CompleteAsync(JsonNode.Parse("""
                {"messages":[{"role":"system","content":"Kurz antworten"},{"role":"user","content":"Hallo"},
                 {"role":"assistant","content":"Hi"},{"role":"user","content":"Wie geht es?"}]}
                """)!.AsObject(), CancellationToken.None);
            await serve;

            Assert.Equal("Hallo zurück", result["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", previous);
        }

        var json = JsonNode.Parse(body!)!;
        Assert.Equal(["user", "assistant", "user"], json["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("Kurz antworten", json["system"]!.GetValue<string>());
        Assert.Null(json["temperature"]);
        Assert.Equal("2026-10-21", version);
        Assert.Equal("Bearer test-schluessel", auth);
    }
}
