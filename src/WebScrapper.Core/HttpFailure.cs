using System.Net;

namespace WebScrapper.Core;

public static class HttpFailure
{
    public static bool RequiresBrowserVerification(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("cf-mitigated", out var values)
        && values.Any(v => v.Equals("challenge", StringComparison.OrdinalIgnoreCase));
    public static string Describe(HttpResponseMessage response)
    {
        var hasCookie = response.RequestMessage?.Headers.Contains("Cookie") == true;
        var hasAuthorization = response.RequestMessage?.Headers.Contains("Authorization") == true;
        var session = $"Cookie {(hasCookie ? "terkirim" : "tidak dikirim")}; Authorization {(hasAuthorization ? "terkirim" : "tidak dikirim")}.";
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return $"401: server meminta autentikasi. {session} Periksa login dan akses akun.";
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (RequiresBrowserVerification(response))
                return $"403: server meminta verifikasi browser. {session} Buka situs di browser untuk memeriksa akses.";
            return $"403: server menolak request. {session} Penyebab belum pasti; periksa akses situs di browser.";
        }
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => "404: halaman tidak ditemukan.",
            HttpStatusCode.TooManyRequests => "429: batas request server tercapai; retry run ini habis.",
            _ => $"HTTP {(int)response.StatusCode}: request gagal."
        };
    }
}
