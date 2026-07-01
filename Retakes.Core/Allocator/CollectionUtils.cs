namespace Retakes.Allocator;

/// <summary>Fisher-Yates shuffle + random-choice helpers used by WeaponHelpers and NadeHelpers.</summary>
internal static class CollectionUtils
{
    public static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public static T Choice<T>(IList<T> list)
    {
        if (list.Count == 0) throw new InvalidOperationException("Cannot choose from an empty list.");
        return list[Random.Shared.Next(list.Count)];
    }
}
