namespace DynamiteTts.Models;

public class TtsModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string? Recipe { get; set; }
    public bool Downloaded { get; set; }
    public string? Description { get; set; }

    public override string ToString() => Id;
}
