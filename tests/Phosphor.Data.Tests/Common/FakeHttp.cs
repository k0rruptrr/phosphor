using System.Net;
using System.Net.Http.Headers;

namespace Phosphor.Data.Tests;

/// <summary>HttpClient, который отвечает из словаря «URL-подстрока → тело». Для тестов источников без сети.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(string UrlPart, byte[] Body, string ContentType)> _routes = new();
    public List<string> Requests { get; } = new();

    public FakeHttp On(string urlPart, string body, string contentType = "application/json; charset=utf-8") =>
        OnBytes(urlPart, System.Text.Encoding.UTF8.GetBytes(body), contentType);

    /// <summary>Сырые байты — для ответов не в UTF-8 (например windows-1251 с XML-декларацией кодировки).</summary>
    public FakeHttp OnBytes(string urlPart, byte[] body, string contentType)
    {
        _routes.Add((urlPart, body, contentType));
        return this;
    }

    /// <summary>Тело из файла в папке Fixtures (копируется рядом с тестовой сборкой), байты как есть.</summary>
    public FakeHttp OnFile(string urlPart, string fixture, string contentType = "application/json") =>
        OnBytes(urlPart, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), contentType);

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        foreach (var (part, body, type) in _routes)
            if (url.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
