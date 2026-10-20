using System.Net;

namespace FinanceForgeLab.Web.Services;

// The app continues listening on loopback. A dedicated HTTPS tunnel may reach
// only the explicitly configured public origin; forwarded IPs never grant access.
public sealed class RemoteAccessPolicy
{
    public Uri? PublicOrigin { get; }
    public bool Enabled => PublicOrigin is not null;
    public string AllowedHosts => "localhost;127.0.0.1;[::1]" + (Enabled ? ";" + PublicOrigin!.Host : "");
    public RemoteAccessPolicy(string? publicOrigin)
    {
        if (string.IsNullOrWhiteSpace(publicOrigin)) return;
        if (!TryOrigin(publicOrigin, out var origin) || origin!.Scheme != "https" || !origin.IsDefaultPort ||
            origin.HostNameType != UriHostNameType.Dns || origin.IsLoopback || !origin.Host.Contains('.'))
            throw new InvalidOperationException("Finance:PublicOrigin deve ser um endereço HTTPS completo, sem caminho, porta, credenciais ou parâmetros.");
        PublicOrigin = origin;
    }
    public bool IsPublic(HttpRequest request) => PublicOrigin is not null &&
        string.Equals(request.Host.Host, PublicOrigin.Host, StringComparison.OrdinalIgnoreCase) && request.Host.Port is null or 443;
    public bool Allows(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is {} ip && !IPAddress.IsLoopback(ip)) return false;
        var request = context.Request;
        if (IsPublic(request))
        {
            if (context.Connection.RemoteIpAddress is null) return false;
            if (request.Headers["X-Forwarded-Proto"].ToString() != "https") return false;
            if (request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost) &&
                !string.Equals(forwardedHost.ToString(), request.Host.Value, StringComparison.OrdinalIgnoreCase)) return false;
            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!TryOrigin(origin, out var parsed) || parsed != PublicOrigin)) return false;
            // Browsers send Origin on writes and WebSocket handshakes. Do not
            // accept cross-site requests, including authenticated Blazor circuits.
            var socket = context.WebSockets.IsWebSocketRequest || request.Headers.Upgrade.Any(v =>
                string.Equals(v, "websocket", StringComparison.OrdinalIgnoreCase)) || HttpMethods.IsConnect(request.Method);
            return origin.Length > 0 || (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && !socket;
        }
        var local = string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(request.Host.Host.Trim('[', ']'), out var hostIp) && IPAddress.IsLoopback(hostIp);
        return local && !request.Headers.Keys.Any(k => k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || k.Equals("CF-Connecting-IP", StringComparison.OrdinalIgnoreCase));
    }
    private static bool TryOrigin(string value, out Uri? origin) => Uri.TryCreate(value, UriKind.Absolute, out origin) &&
        origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
}
