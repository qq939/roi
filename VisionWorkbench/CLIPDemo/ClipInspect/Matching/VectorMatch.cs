namespace ClipInspect.Matching;

public sealed class VectorMatch
{
    public required int Rank { get; init; }
    public required float Similarity { get; init; }
    public string? ImagePath { get; init; }
    public string? Prompt { get; init; }
}
