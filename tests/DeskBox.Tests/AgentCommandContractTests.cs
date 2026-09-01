using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AgentCommandContractTests
{
    [Fact]
    public void AgentResponseSerializesWithStableCamelCaseShape()
    {
        JsonElement result = JsonSerializer.SerializeToElement(
            new AgentPingResult("pong"),
            AgentJsonContext.Default.AgentPingResult);
        string json = JsonSerializer.Serialize(
            new AgentResponse("request-1", true, result),
            AgentJsonContext.Default.AgentResponse);

        Assert.Contains("\"id\":\"request-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":{\"message\":\"pong\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentErrorSerializesWithoutAnEmptyResultField()
    {
        string json = JsonSerializer.Serialize(
            new AgentResponse(
                "request-2",
                false,
                Error: new AgentError("confirmation_required", "Confirm first.")),
            AgentJsonContext.Default.AgentResponse);

        Assert.Contains("\"error\":{\"code\":\"confirmation_required\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"result\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellSystemEntryResultSerializesWithStableCamelCaseShape()
    {
        JsonElement result = JsonSerializer.SerializeToElement(
            new AgentShellSystemEntryResult(
                "widget-1",
                "recycle_bin",
                @"C:\DeskBox\Recycle Bin.lnk",
                "Recycle Bin",
                true),
            AgentJsonContext.Default.AgentShellSystemEntryResult);

        string json = result.GetRawText();
        Assert.Contains("\"widgetId\":\"widget-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"systemId\":\"recycle_bin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"desktopIconHidden\":true", json, StringComparison.Ordinal);
    }
}
