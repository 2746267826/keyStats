using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KeyStats.Services;

/// <summary>
/// 轻量级 HTTP API 服务，供 PIM 守护程序读取键盘鼠标统计数据。
/// 监听 http://127.0.0.1:18080/api/stats/，返回 StatsManager 的当前快照。
/// 使用 .NET 原生 HttpListener，零外部依赖。
/// </summary>
public sealed class ApiService : IDisposable
{
    private readonly HttpListener _listener = new()
    {
        IgnoreWriteExceptions = true,
    };

    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public void Start()
    {
        _listener.Prefixes.Add("http://127.0.0.1:18080/");
        _cts = new CancellationTokenSource();

        try
        {
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine("[ApiService] Started on http://127.0.0.1:18080/");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiService] Failed to start: {ex.Message}");
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // GetContextAsync 本身没有 CancellationToken 重载，
                // 所以用 RegisterWaitForSingleObject 超时退出
                var task = _listener.GetContextAsync();
                using var reg = ct.Register(() => _listener.Stop());

                HttpListenerContext ctx;
                try
                {
                    ctx = await task;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                // 每个请求独立处理，不阻塞监听循环
                _ = Task.Run(() => HandleRequest(ctx));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath?.Trim('/') ?? "";
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

            if (path.Equals("api/stats", StringComparison.OrdinalIgnoreCase))
            {
                // 读当前统计数据（线程安全快照）
                var stats = StatsManager.Instance.CurrentStats;
                var json = JsonSerializer.Serialize(stats, JsonOptions);
                var buffer = Encoding.UTF8.GetBytes(json);

                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentLength64 = 0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiService] Request error: {ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentLength64 = 0;
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
    }
}
