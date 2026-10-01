// Only the three named method bodies are injected. Fields bind strictly to the
// existing DataSeries or the two additions; Quantile binds to the original IL.
#pragma warning disable CS0649
internal sealed class OptimizedTemplate
{
    private int[] values = null!;
    private bool[] used = null!;
    private int next, count, usedCount, previous, median, p90, max;
    private int[] _ordered = null!;
    private int _orderedCount;

    public void StartNextFrame()
    {
        if (used[next])
        {
            previous = values[next];
            OrderedInsert(previous);
        }
        if (count < values.Length) count++;
        next = (next + 1) % values.Length;
        usedCount = _orderedCount;
        if (usedCount > 0)
        {
            // Retain float conversion/Lerp/truncation, including max at q=1.
            median = Quantile(_ordered, usedCount, 0.5f);
            p90 = Quantile(_ordered, usedCount, 0.9f);
            max = Quantile(_ordered, usedCount, 1f);
        }
        else
        {
            previous = median = p90 = max = 0;
        }
        // Statistics include the expiring slot, just as the original scan does.
        // During partial fill after Reset, used[next] can be stale but outside
        // the original [0,count) prefix; it was never in the ordered history.
        if (next < count && used[next]) OrderedRemove(values[next]);
        values[next] = 0;
        used[next] = false;
    }

    private void OrderedInsert(int value)
    {
        int low = 0, high = _orderedCount;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_ordered[middle] < value) low = middle + 1;
            else high = middle;
        }
        if (low < _orderedCount)
            Array.Copy(_ordered, low, _ordered, low + 1, _orderedCount - low);
        _ordered[low] = value;
        _orderedCount++;
    }

    private void OrderedRemove(int value)
    {
        int low = 0, high = _orderedCount;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_ordered[middle] < value) low = middle + 1;
            else high = middle;
        }
        _orderedCount--;
        if (low < _orderedCount)
            Array.Copy(_ordered, low + 1, _ordered, low, _orderedCount - low);
    }

    private static int Quantile(int[] values, int count, float quantile) =>
        throw new InvalidOperationException("Template anchor only; never injected or executed");
}
#pragma warning restore CS0649
