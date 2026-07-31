using ClipInspect.Storage;

namespace ClipInspect.Matching;

public static class TopKMatcher
{
    public static (float Score, IReadOnlyList<VectorMatch> Matches) ScoreImages(
        ReadOnlySpan<float> queryFeature,
        IReadOnlyList<ImageCacheItem> items,
        int topK)
    {
        var scored = new List<(int Index, float Similarity)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            scored.Add((i, VectorMath.Dot(queryFeature, items[i].Feature)));
        }

        scored.Sort((left, right) => right.Similarity.CompareTo(left.Similarity));

        var count = Math.Min(topK, items.Count);
        var matches = new VectorMatch[count];
        for (var i = 0; i < count; i++)
        {
            var item = scored[i];
            matches[i] = new VectorMatch
            {
                Rank = i + 1,
                Similarity = item.Similarity,
                ImagePath = items[item.Index].ImagePath
            };
        }

        return (Mean(matches), matches);
    }

    public static (float Score, IReadOnlyList<VectorMatch> Matches) ScoreTexts(
        ReadOnlySpan<float> queryFeature,
        IReadOnlyList<TextCacheItem> items,
        int topK)
    {
        var scored = new List<(int Index, float Similarity)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            scored.Add((i, VectorMath.Dot(queryFeature, items[i].Feature)));
        }

        scored.Sort((left, right) => right.Similarity.CompareTo(left.Similarity));

        var count = Math.Min(topK, items.Count);
        var matches = new VectorMatch[count];
        for (var i = 0; i < count; i++)
        {
            var item = scored[i];
            matches[i] = new VectorMatch
            {
                Rank = i + 1,
                Similarity = item.Similarity,
                Prompt = items[item.Index].Prompt
            };
        }

        return (Mean(matches), matches);
    }

    private static float Mean(IReadOnlyList<VectorMatch> matches)
    {
        if (matches.Count == 0)
        {
            return float.NaN;
        }

        var sum = 0f;
        for (var i = 0; i < matches.Count; i++)
        {
            sum += matches[i].Similarity;
        }

        return sum / matches.Count;
    }
}
