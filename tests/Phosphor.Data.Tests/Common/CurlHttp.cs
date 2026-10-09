using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Phosphor.Data.Tests;

/// <summary>
/// Транспорт для живых проверок: запросы идут через curl.exe. Нужен, потому что тестовый хост на этой машине
/// режет сокеты фаерволом (WSAEACCES), а curl проходит. Код источников при этом не меняется.
/// </summary>
public sealed class CurlHttp : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var tmp = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo("curl.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-sS");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("60");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("PHOSPHOR-screensaver/0.1 (ascii screensaver; personal use)");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("%{http_code}");
            psi.ArgumentList.Add(url);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("curl.exe не запустился");
            var codeText = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new HttpRequestException($"curl: код {p.ExitCode} для {url}");

            var status = (HttpStatusCode)int.Parse(codeText.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var bytes = await File.ReadAllBytesAsync(tmp, ct);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaType(url));
            return new HttpResponseMessage(status) { Content = content, RequestMessage = request };
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    private static string MediaType(string url)
    {
        if (url.Contains("request=GetCapabilities", StringComparison.OrdinalIgnoreCase)) return "application/xml";
        if (url.Contains("request=GetMap", StringComparison.OrdinalIgnoreCase) && url.Contains("image/png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (url.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (url.Contains("FORMAT=image/jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        return "application/octet-stream";
    }
}
