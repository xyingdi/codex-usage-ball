using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageBall.Models;

namespace CodexUsageBall.Services;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Queue<string> _diagnostics = new();
    private Process? _process;
    private CancellationTokenSource? _processCancellation;
    private StreamWriter? _writer;
    private Task? _readLoop;
    private Task? _errorLoop;
    private int _requestId;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler? RateLimitsChanged;

    public string? LastDiagnostic
    {
        get
        {
            lock (_diagnostics)
            {
                return _diagnostics.LastOrDefault();
            }
        }
    }

    public async Task<UsageSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var accountTask = SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15),
                cancellationToken);

            var rateLimitsTask = SendRequestAsync(
                "account/rateLimits/read",
                parameters: null,
                TimeSpan.FromSeconds(45),
                cancellationToken);

            var accountResult = await accountTask.ConfigureAwait(false);
            var rateLimitsResult = await rateLimitsTask.ConfigureAwait(false);

            var (planType, authenticated) = ParseAccount(accountResult);
            if (!authenticated)
            {
                throw new CodexConnectionException("请先在 Codex 中登录 ChatGPT 账户。");
            }

            var buckets = ParseRateLimits(rateLimitsResult);
            if (buckets.Count == 0)
            {
                throw new CodexConnectionException("Codex 暂未返回可显示的额度窗口。");
            }

            return new UsageSnapshot(
                planType ?? buckets.Select(bucket => bucket.PlanType).FirstOrDefault(value => value is not null),
                buckets,
                DateTimeOffset.Now);
        }
        catch (CodexConnectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexConnectionException("读取 Codex 用量超时，请稍后重试。");
        }
        catch (Exception exception)
        {
            throw new CodexConnectionException("无法读取 Codex 用量。", exception);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopProcessLocked();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcessLocked();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized && _process is { HasExited: false })
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _process is { HasExited: false })
            {
                return;
            }

            StopProcessLocked();
            var codexExecutable = LocateCodexExecutable();
            if (codexExecutable is null)
            {
                throw new CodexConnectionException(
                    "未找到 Codex CLI。请先安装或启动 Codex 桌面应用。");
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = codexExecutable,
                    Arguments = "app-server",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                },
                EnableRaisingEvents = true
            };

            try
            {
                if (!process.Start())
                {
                    throw new CodexConnectionException("Codex App Server 未能启动。");
                }
            }
            catch (Exception exception) when (exception is not CodexConnectionException)
            {
                process.Dispose();
                throw new CodexConnectionException("Codex App Server 启动失败。", exception);
            }

            _process = process;
            _writer = process.StandardInput;
            _writer.NewLine = "\n";
            _writer.AutoFlush = true;
            _processCancellation = new CancellationTokenSource();
            _readLoop = Task.Run(
                () => ReadLoopAsync(process.StandardOutput, _processCancellation.Token),
                CancellationToken.None);
            _errorLoop = Task.Run(
                () => ErrorLoopAsync(process.StandardError, _processCancellation.Token),
                CancellationToken.None);

            await SendRequestCoreAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new
                        {
                            name = "codex_usage_ball",
                            title = "Codex Usage Ball",
                            version = "1.8.7"
                        }
                    },
                    TimeSpan.FromSeconds(20),
                    cancellationToken)
                .ConfigureAwait(false);

            await SendNotificationAsync("initialized", new { }, cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            StopProcessLocked();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await SendRequestCoreAsync(method, parameters, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> SendRequestCoreAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new CodexConnectionException("无法创建 Codex 请求。");
        }

        try
        {
            await WriteMessageAsync(new RpcRequest(method, id, parameters), cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            using var registration = timeoutCancellation.Token.Register(() =>
                completion.TrySetCanceled(timeoutCancellation.Token));

            JsonElement response;
            try
            {
                response = await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CodexConnectionException($"Codex 请求 {method} 超时。");
            }

            if (response.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                throw new CodexConnectionException(message ?? $"Codex 请求 {method} 失败。");
            }

            if (!response.TryGetProperty("result", out var result))
            {
                throw new CodexConnectionException($"Codex 请求 {method} 返回了无效响应。");
            }

            return result.Clone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new RpcNotification(method, parameters), cancellationToken);
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new CodexConnectionException("Codex App Server 尚未连接。");
        var json = JsonSerializer.Serialize(message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CodexConnectionException("无法向 Codex App Server 发送请求。", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind == JsonValueKind.Number
                        && idElement.TryGetInt32(out var id)
                        && _pending.TryGetValue(id, out var completion))
                    {
                        completion.TrySetResult(root.Clone());
                        continue;
                    }

                    if (root.TryGetProperty("method", out var methodElement)
                        && methodElement.GetString() is { } method
                        && string.Equals(
                            method,
                            "account/rateLimits/updated",
                            StringComparison.Ordinal))
                    {
                        RateLimitsChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (JsonException exception)
                {
                    AddDiagnostic($"无法解析 Codex 响应：{exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            AddDiagnostic($"Codex 数据连接已断开：{exception.Message}");
        }
        finally
        {
            FailPending(new CodexConnectionException("Codex App Server 连接已断开。"));
        }
    }

    private async Task ErrorLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    AddDiagnostic(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            AddDiagnostic(exception.Message);
        }
    }

    private void AddDiagnostic(string line)
    {
        lock (_diagnostics)
        {
            _diagnostics.Enqueue(line);
            while (_diagnostics.Count > 12)
            {
                _diagnostics.Dequeue();
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(exception);
        }
    }

    private void StopProcessLocked()
    {
        _initialized = false;
        _processCancellation?.Cancel();
        _writer = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort shutdown of a child process owned by this client.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        _processCancellation?.Dispose();
        _processCancellation = null;
        _readLoop = null;
        _errorLoop = null;
        FailPending(new CodexConnectionException("Codex App Server 已重新启动。"));
    }

    private static string? LocateCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            configured,
            Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe"),
            Path.Combine(localAppData, "Programs", "Codex", "resources", "codex.exe"),
            Path.Combine(localAppData, "Programs", "ChatGPT", "resources", "codex.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static (string? PlanType, bool Authenticated) ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account)
            || account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (null, false);
        }

        return (ReadString(account, "planType"), true);
    }

    private static List<QuotaBucket> ParseRateLimits(
        JsonElement result)
    {
        var buckets = new List<QuotaBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId)
            && byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                buckets.Add(ParseBucket(property.Value, property.Name));
            }
        }

        if (buckets.Count == 0
            && result.TryGetProperty("rateLimits", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            buckets.Add(ParseBucket(legacy, "codex"));
        }

        return buckets;
    }

    private static QuotaBucket ParseBucket(JsonElement element, string fallbackId)
    {
        return new QuotaBucket(
            ReadString(element, "limitId") ?? fallbackId,
            ReadString(element, "limitName"),
            ReadString(element, "planType"),
            ParseWindow(element, "primary"),
            ParseWindow(element, "secondary"));
    }

    private static QuotaWindow? ParseWindow(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = ReadDouble(window, "usedPercent") ?? 0d;
        var duration = ReadInt(window, "windowDurationMins") ?? 0;
        DateTimeOffset? resetsAt = null;
        if (ReadLong(window, "resetsAt") is { } timestamp)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Keep the window usable even when an upstream timestamp is malformed.
            }
        }

        return new QuotaWindow(usedPercent, duration, resetsAt);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcessLocked();
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
            _writeLock.Dispose();
        }
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("params")] object? Params);

    private sealed record RpcNotification(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object Params);
}
