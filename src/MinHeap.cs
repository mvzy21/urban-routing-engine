namespace UrbanRouting;

/// <summary>
/// A hand-written binary MIN-HEAP: a complete binary tree stored in an array.
/// For the item at index i: parent = (i - 1) / 2, left child = 2i + 1, right child = 2i + 2.
/// The smallest priority is always at index 0 (the root).
///
///   Push (insert)      add at the end of the array, then "bubble up" while smaller than the parent   O(log n)
///   TryPop (delete)    take the root, move the last item into the root, then "bubble down"          O(log n)
///   Peek               look at the root                                                             O(1)
///
/// This is the priority queue behind Dijkstra and reachability.
/// There is no decrease-key: the algorithms push a node again with its new, lower cost and skip stale copies
/// ("lazy deletion"), which keeps the heap simple and still costs O(log n) per operation.
/// </summary>
public sealed class MinHeap<T>
{
    private (T Item, double Priority)[] items = new (T, double)[16];

    public int Count { get; private set; }

    public void Push(T item, double priority)
    {
        if (double.IsNaN(priority)) throw new ArgumentException("A heap priority cannot be NaN.");
        if (Count == items.Length) Array.Resize(ref items, items.Length * 2);   // grow like a List: amortised O(1)

        int i = Count++;
        items[i] = (item, priority);
        while (i > 0)                                                            // bubble up
        {
            int parent = (i - 1) / 2;
            if (items[parent].Priority <= items[i].Priority) break;              // heap property holds
            (items[parent], items[i]) = (items[i], items[parent]);
            i = parent;
        }
    }

    public bool TryPop(out T item, out double priority)
    {
        if (Count == 0)
        {
            item = default!;
            priority = double.NaN;
            return false;
        }

        (item, priority) = items[0];                                             // the root is the minimum
        Count--;
        items[0] = items[Count];                                                 // last leaf moves to the root
        items[Count] = default;

        int i = 0;
        while (true)                                                             // bubble down
        {
            int left = 2 * i + 1, right = left + 1, smallest = i;
            if (left < Count && items[left].Priority < items[smallest].Priority) smallest = left;
            if (right < Count && items[right].Priority < items[smallest].Priority) smallest = right;
            if (smallest == i) break;                                            // smaller than both children
            (items[smallest], items[i]) = (items[i], items[smallest]);
            i = smallest;
        }
        return true;
    }

    public double PeekPriority =>
        Count > 0 ? items[0].Priority : throw new InvalidOperationException("The heap is empty.");

    /// <summary>Checks the heap property on every parent/child pair. O(n); used by the tests only.</summary>
    public bool IsValid()
    {
        for (int i = 1; i < Count; i++)
            if (items[(i - 1) / 2].Priority > items[i].Priority) return false;
        return true;
    }
}
