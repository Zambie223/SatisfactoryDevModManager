using System.Diagnostics;

namespace Sdmm.Core.Diagnostics;

/// <summary>
/// Logs each web request to the detailed log (<see cref="FileLog"/>): method, address, result and time taken. Headers
/// (where tokens live) are never logged, and a query string that looks like it carries a signature or token is cut.
/// </summary>
public sealed class LoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!FileLog.Enabled) return await base.SendAsync(request, ct);
        var sw = Stopwatch.StartNew();
        var what = $"{request.Method} {Describe(request.RequestUri)}";
        try
        {
            var response = await base.SendAsync(request, ct);
            FileLog.Write("http", $"{what} -> {(int)response.StatusCode} ({sw.ElapsedMilliseconds} ms)");
            return response;
        }
        catch (Exception ex)
        {
            FileLog.Write("http", $"{what} FAILED after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>The address without anything secret-looking in its query string.</summary>
    public static string Describe(Uri? uri)
    {
        if (uri is null) return "(no address)";
        var q = uri.Query;
        bool secret = q.Contains("sig", StringComparison.OrdinalIgnoreCase) || q.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                      q.Contains("key", StringComparison.OrdinalIgnoreCase) || q.Contains("credential", StringComparison.OrdinalIgnoreCase);
        return secret ? uri.GetLeftPart(UriPartial.Path) + "?(hidden)" : uri.GetLeftPart(UriPartial.Query);
    }
}
