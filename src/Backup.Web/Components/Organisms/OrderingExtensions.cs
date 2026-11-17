namespace Backup.Web.Components.Organisms;

internal static class OrderingExtensions
{
    /// <summary>ThenBy o ThenByDescending según <paramref name="descending"/>.</summary>
    public static IOrderedEnumerable<T> Then<T, TKey>(this IOrderedEnumerable<T> source, Func<T, TKey> key, bool descending) =>
        descending ? source.ThenByDescending(key) : source.ThenBy(key);
}
