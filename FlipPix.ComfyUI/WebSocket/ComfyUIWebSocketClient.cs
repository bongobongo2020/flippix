using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FlipPix.Core.Interfaces;
using FlipPix.ComfyUI.Models;

namespace FlipPix.ComfyUI.WebSocket;

public class ComfyUIWebSocketClient : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly string _baseUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed = false;
    private readonly Queue<WebSocketMessage> _messageQueue = new();
    private readonly object _lockObject = new();
    private string? _clientId;
    private const int _maxReconnectAttempts = 10;
    private const int _reconnectDelayMs = 2000;
    private bool _isReconnecting = false;

    public event EventHandler<WebSocketMessage>? MessageReceived;
    public event EventHandler<string>? ConnectionStatusChanged;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public ComfyUIWebSocketClient(IAppLogger logger, string baseUrl)
    {
        _logger = logger;
        _baseUrl = baseUrl;
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            _clientId = clientId;
            _logger.LogInfo("Connecting to ComfyUI WebSocket: {BaseUrl}", _baseUrl);

            _cancellationTokenSource = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();

            var wsUrl = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            var uri = new Uri($"{wsUrl}/ws?clientId={clientId}");

            await _webSocket.ConnectAsync(uri, cancellationToken);

            _logger.LogInfo("WebSocket connected successfully");
            ConnectionStatusChanged?.Invoke(this, "Connected");

            // Start listening for messages
            _ = Task.Run(() => ListenForMessagesAsync(_cancellationTokenSource.Token), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect WebSocket");
            ConnectionStatusChanged?.Invoke(this, "Failed");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                _logger.LogInfo("Disconnecting WebSocket");

                _cancellationTokenSource?.Cancel();
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);

                ConnectionStatusChanged?.Invoke(this, "Disconnected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during WebSocket disconnect");
        }
    }

    private async Task ReconnectAsync()
    {
        if (_isReconnecting || _disposed)
        {
            _logger.LogDebug("Reconnection already in progress or client disposed, skipping");
            return;
        }

        if (string.IsNullOrEmpty(_clientId))
        {
            _logger.LogError("Cannot reconnect: clientId is null or empty");
            ConnectionStatusChanged?.Invoke(this, "Failed");
            return;
        }

        _isReconnecting = true;
        ConnectionStatusChanged?.Invoke(this, "Reconnecting");

        try
        {
            for (int attempt = 1; attempt <= _maxReconnectAttempts; attempt++)
            {
                try
                {
                    _logger.LogInfo("WebSocket reconnection attempt {Attempt}/{MaxAttempts}", attempt, _maxReconnectAttempts);

                    // Clean up old connection
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();
                    _webSocket?.Dispose();

                    // Create new connection
                    _cancellationTokenSource = new CancellationTokenSource();
                    _webSocket = new ClientWebSocket();

                    var wsUrl = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
                    var uri = new Uri($"{wsUrl}/ws?clientId={_clientId}");

                    await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                    _logger.LogInfo("WebSocket reconnected successfully on attempt {Attempt}", attempt);
                    ConnectionStatusChanged?.Invoke(this, "Reconnected");

                    // Start listening for messages
                    _ = Task.Run(() => ListenForMessagesAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                    _isReconnecting = false;
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WebSocket reconnection attempt {Attempt} failed", attempt);

                    if (attempt == _maxReconnectAttempts)
                    {
                        _logger.LogError("WebSocket reconnection failed after {MaxAttempts} attempts", _maxReconnectAttempts);
                        ConnectionStatusChanged?.Invoke(this, "Failed");
                        _isReconnecting = false;
                        return;
                    }

                    // Exponential backoff: delay = baseDelay * 2^(attempt-1), capped at 30 seconds
                    var delay = Math.Min(_reconnectDelayMs * (int)Math.Pow(2, attempt - 1), 30000);
                    _logger.LogInfo("Waiting {Delay}ms before next reconnection attempt", delay);
                    await Task.Delay(delay, _cancellationTokenSource.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during WebSocket reconnection");
            ConnectionStatusChanged?.Invoke(this, "Failed");
            _isReconnecting = false;
        }
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        _logger.LogWarning("WebSocket not connected, attempting reconnection");
        await ReconnectAsync();
    }

    private async Task ListenForMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInfo("WebSocket closed by server, initiating reconnection");
                        ConnectionStatusChanged?.Invoke(this, "Closed");
                        _ = Task.Run(() => ReconnectAsync());
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(ms.ToArray(), 0, (int)ms.Length);
                    ProcessMessage(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInfo("WebSocket message listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket message listener, initiating reconnection");
            ConnectionStatusChanged?.Invoke(this, "Error");
            _ = Task.Run(() => ReconnectAsync());
        }
    }

    private void ProcessMessage(string messageText)
    {
        try
        {
            var message = ParseMessage(messageText);

            lock (_lockObject)
            {
                _messageQueue.Enqueue(message);
            }

            MessageReceived?.Invoke(this, message);

            _logger.LogInfo("WebSocket message received: {MessageType}", message.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process WebSocket message: {Message}", messageText);
        }
    }

    public WebSocketMessage ParseMessage(string messageText)
    {
        try
        {
            // Skip non-JSON messages (binary data fragments)
            if (string.IsNullOrWhiteSpace(messageText) || 
                (!messageText.StartsWith("{") && !messageText.StartsWith("[")))
            {
                return new UnknownMessage { RawData = messageText };
            }
            
            using var document = JsonDocument.Parse(messageText);
            var root = document.RootElement;
            
            if (root.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString() ?? "unknown";
                
                return messageType switch
                {
                    "status" => JsonSerializer.Deserialize<StatusMessage>(messageText) ?? new StatusMessage(),
                    "execution_start" => JsonSerializer.Deserialize<ExecutionStartMessage>(messageText) ?? new ExecutionStartMessage(),
                    "executing" => JsonSerializer.Deserialize<ExecutingMessage>(messageText) ?? new ExecutingMessage(),
                    "progress" => JsonSerializer.Deserialize<ProgressMessage>(messageText) ?? new ProgressMessage(),
                    "execution_complete" => JsonSerializer.Deserialize<ExecutionCompleteMessage>(messageText) ?? new ExecutionCompleteMessage(),
                    _ => new WebSocketMessage { Type = messageType, RawData = messageText }
                };
            }
            
            return new WebSocketMessage { Type = "unknown", RawData = messageText };
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("Failed to parse JSON message (likely fragmented): {Error}", ex.Message);
            return new UnknownMessage { RawData = messageText };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse WebSocket message");
            return new UnknownMessage { RawData = messageText };
        }
    }

    public List<WebSocketMessage> GetPendingMessages()
    {
        lock (_lockObject)
        {
            var messages = new List<WebSocketMessage>(_messageQueue);
            _messageQueue.Clear();
            return messages;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _webSocket?.Dispose();
            _disposed = true;
        }
    }
}