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

    private async Task ListenForMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(message);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInfo("WebSocket closed by server");
                    ConnectionStatusChanged?.Invoke(this, "Closed");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInfo("WebSocket message listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket message listener");
            ConnectionStatusChanged?.Invoke(this, "Error");
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