using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// Local, newline-delimited JSON transport for the DeskBox agent command API.
/// The pipe is restricted to the current Windows user by PipeOptions.CurrentUserOnly.
/// </summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly AgentCommandService _commandService;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public AgentPipeServer(string pipeName, AgentCommandService commandService)
    {
        _pipeName = pipeName;
        _commandService = commandService;
    }

    public void Start()
    {
        if (_serverTask is not null)
        {
            return;
        }

        _serverTask = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                await HandleClientAsync(pipe, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                App.Log($"[Agent] Pipe server iteration failed: {ex}");
                await Task.Delay(250, _shutdown.Token);
            }
        }
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AgentResponse response;
        if (line.Length > 1024 * 1024)
        {
            response = new AgentResponse(
                string.Empty,
                false,
                Error: new AgentError("request_too_large", "The request exceeds the 1 MB limit."));
        }
        else
        {
            response = await ParseAndExecuteAsync(line, cancellationToken);
        }

        string json = JsonSerializer.Serialize(response, AgentJsonContext.Default.AgentResponse);
        await writer.WriteLineAsync(json);
    }

    private async Task<AgentResponse> ParseAndExecuteAsync(
        string line,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string id = root.TryGetProperty("id", out JsonElement idElement) &&
                        idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            string method = root.TryGetProperty("method", out JsonElement methodElement) &&
                            methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement.Clone()
                : default;

            if (string.IsNullOrWhiteSpace(method))
            {
                return new AgentResponse(
                    id,
                    false,
                    Error: new AgentError("invalid_request", "The request requires a non-empty method."));
            }

            return await _commandService.ExecuteAsync(
                new AgentRequest(id, method, parameters),
                cancellationToken);
        }
        catch (JsonException ex)
        {
            return new AgentResponse(
                string.Empty,
                false,
                Error: new AgentError("invalid_json", ex.Message));
        }
    }
}
