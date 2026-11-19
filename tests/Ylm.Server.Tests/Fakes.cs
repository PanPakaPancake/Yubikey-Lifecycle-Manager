using System.Collections.Concurrent;
using System.Net;
using Ylm.Server.Data;
using Ylm.Server.Integrations;

namespace Ylm.Server.Tests;

/// <summary>Captures outgoing HTTP calls (webhooks) and answers with a configurable response.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

    public ConcurrentQueue<Call> Calls { get; } = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Calls.Enqueue(new Call(request.Method, request.RequestUri!, headers, body));
        return Respond(request, body);
    }

    // The handler is reused by the client factory; never dispose it.
    protected override void Dispose(bool disposing) { }
}

public sealed class FakeMailSender : IMailSender
{
    public ConcurrentQueue<(SmtpSettings Settings, string? Password, OutgoingMail Mail)> Sent { get; } = new();
    public Exception? Fail { get; set; }

    public Task SendAsync(SmtpSettings settings, string? password, OutgoingMail mail, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Sent.Enqueue((settings, password, mail));
        return Task.CompletedTask;
    }
}
