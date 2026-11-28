using Ylm.Contracts.Localization;

namespace Ylm.Server.Infrastructure;

/// <summary>
/// Sprache einer Anfrage für Fehlermeldungen, PDFs und Exporte: Kopfzeile X-YLM-Language (sendet die Weboberfläche bei jedem
/// API-Aufruf), sonst Cookie ylm-lang (für Downloads per Link), sonst Accept-Language des Browsers, sonst Deutsch.
/// </summary>
public sealed class RequestLanguageMiddleware(RequestDelegate next)
{
    public const string Header = "X-YLM-Language";
    public const string Cookie = "ylm-lang";

    public async Task InvokeAsync(HttpContext context)
    {
        using (L.Use(Resolve(context.Request)))
            await next(context);
    }

    public static string Resolve(HttpRequest request)
    {
        if (L.Normalize(request.Headers[Header].ToString()) is { } h) return h;
        if (request.Cookies.TryGetValue(Cookie, out var c) && L.Normalize(c) is { } fromCookie) return fromCookie;
        var accept = request.Headers.AcceptLanguage.ToString();
        foreach (var part in accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (L.Normalize(part.Split(';')[0]) is { } a) return a;
        return L.German;
    }
}
