using System.Text.Json;

namespace WebApplication1.Models;

public sealed class ConversationRequest
{
    public string? Text { get; init; }

    public string? Message { get; init; }

    public JsonElement? States { get; init; }

    public string? GetText()
    {
        return !string.IsNullOrWhiteSpace(Text) ? Text : Message;
    }
}
