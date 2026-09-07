namespace VisualRelay.Core.Queue;

// ReSharper disable once UnusedType.Global — partial of RelayQueueController
public sealed partial class RelayQueueController
{
    // ReSharper disable once UnusedMember.Global — public symmetric reorder API;
    // the counterpart MoveDown is exercised by RelayQueueControllerTests.
    public void MoveUp(string taskId)
    {
        var index = IndexOf(taskId);
        if (index > 0) Tasks.Move(index, index - 1);
    }

    public void MoveDown(string taskId)
    {
        var index = IndexOf(taskId);
        if (index >= 0 && index < Tasks.Count - 1) Tasks.Move(index, index + 1);
    }

    /// <summary>
    /// Stable-reorders <see cref="Tasks"/> to match <paramref name="orderedIds"/>.
    /// Ids absent from <paramref name="orderedIds"/> keep their original relative
    /// order at the end. In-memory only — order is never persisted. Used by the
    /// GUI drain to align this controller with the app's visible order.
    /// </summary>
    public void ApplyOrder(IReadOnlyList<string> orderedIds)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedIds.Count; i++) rank.TryAdd(orderedIds[i], i);
        var sorted = Tasks
            .Select((t, i) => (t, key: rank.GetValueOrDefault(t.Id, int.MaxValue), orig: i))
            .OrderBy(x => x.key).ThenBy(x => x.orig)
            .Select(x => x.t).ToList();
        Tasks.Clear();
        foreach (var t in sorted) Tasks.Add(t);
    }
}
