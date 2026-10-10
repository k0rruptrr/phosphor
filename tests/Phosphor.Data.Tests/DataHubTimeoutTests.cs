using System.Net;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

/// <summary>Таймаут на HTTP-запрос источника: IDataSource.Timeout применяется хабом, а не общим клиентом.</summary>
public sealed class DataHubTimeoutTests
{
    private sealed record TestSnap(DateTimeOffset FetchedAt) : DataSnapshot(FetchedAt, false);

    private sealed class SlowSource : IDataSource
    {
        public SlowSource(TimeSpan timeout) => Timeout = timeout;

        public string Key => "slowtest";
        public string Name => "slowtest.src";
        public TimeSpan Interval => TimeSpan.FromMinutes(10);
        public TimeSpan Timeout { get; }

        public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
        {
            await http.GetStringAsync("https://example.test/slow", ct).ConfigureAwait(false);
            return new TestSnap(DateTimeOffset.Now);
        }
    }

    /// <summary>Ответ приходит через delay: имитирует медленную линию.</summary>
    private sealed class DelayHttp : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayHttp(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public void FireSource_TimeoutIs180Seconds_DefaultIs15()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), ((IDataSource)new FireSource(() => null, 500)).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), ((IDataSource)new DefaultTimeoutProbe()).Timeout);
    }

    [Fact]
    public async Task SlowResponse_WithinTimeout_Succeeds()
    {
        // Ответ через 20 с при таймауте источника 30 с — успех.
        var (ok, status) = await RunOnce(delay: TimeSpan.FromSeconds(20), timeout: TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(45));
        Assert.True(ok, "снимок не появился");
        Assert.Null(status?.Kind);
    }

    [Fact]
    public async Task SlowResponse_BeyondTimeout_ReportsTimeoutStatus()
    {
        // Ответ через 20 с при таймауте источника 1 с — ошибка TIMEOUT, как у остальных источников.
        var (ok, status) = await RunOnce(delay: TimeSpan.FromSeconds(20), timeout: TimeSpan.FromSeconds(1), wait: TimeSpan.FromSeconds(15));
        Assert.False(ok);
        Assert.Equal("TIMEOUT", status?.Kind);
    }

    private static async Task<(bool Ok, SourceStatusSnapshot? Status)> RunOnce(TimeSpan delay, TimeSpan timeout, TimeSpan wait)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phosphor-hub-timeout-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hub = new DataHub(new IDataSource[] { new SlowSource(timeout) }, online: true, _ => { },
                merge: null, cacheDir: dir, handler: new DelayHttp(delay));
            hub.Start();
            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                if (hub.TryGet("slowtest", out _, out _))
                {
                    hub.TryGet("slowtest.status", out var okSt, out _);
                    return (true, okSt as SourceStatusSnapshot);
                }
                if (hub.TryGet("slowtest.status", out var st, out _) && st is SourceStatusSnapshot { Kind: not null } bad)
                    return (false, bad);
                await Task.Delay(100).ConfigureAwait(false);
            }
            hub.TryGet("slowtest.status", out var last, out _);
            return (false, last as SourceStatusSnapshot);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Источник без переопределения Timeout — проверка значения по умолчанию интерфейса.</summary>
    private sealed class DefaultTimeoutProbe : IDataSource
    {
        public string Key => "probe";
        public string Name => "probe";
        public TimeSpan Interval => TimeSpan.FromMinutes(10);
        public Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct) => Task.FromResult<DataSnapshot?>(null);
    }
}
