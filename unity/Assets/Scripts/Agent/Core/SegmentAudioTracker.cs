using System.Collections.Generic;

/// <summary>
/// Maps spoken segment ids onto positions in the audio stream so the UI knows which line is playing.
/// Assumes one audio chunk per spoken segment, in order; segments and chunks may arrive in either order.
/// Positions are absolute sample counts (samples written to the player since start).
/// </summary>
public class SegmentAudioTracker
{
    struct Mark { public long start; public int id; }

    readonly Queue<int> _pendingIds = new Queue<int>();     // spoken segments waiting for their audio
    readonly Queue<long> _orphanStarts = new Queue<long>(); // audio chunks waiting for their segment
    readonly List<Mark> _marks = new List<Mark>();

    public int MarkCount { get { return _marks.Count; } }

    public void Clear() { _pendingIds.Clear(); _orphanStarts.Clear(); _marks.Clear(); }

    public void OnSegment(int id, bool spoken)
    {
        if (!spoken) return; // silent (tips) have no audio
        foreach (var m in _marks) if (m.id == id) return; // repeated event for an already mapped segment
        if (_orphanStarts.Count > 0) _marks.Add(new Mark { start = _orphanStarts.Dequeue(), id = id });
        else _pendingIds.Enqueue(id);
    }

    public void OnAudioChunk(long startSample)
    {
        if (_pendingIds.Count > 0) _marks.Add(new Mark { start = startSample, id = _pendingIds.Dequeue() });
        else _orphanStarts.Enqueue(startSample);
    }

    /// <summary>Segment id whose audio contains sample position `consumed`; -1 before the first mark.</summary>
    public int Current(long consumed)
    {
        int id = -1; long best = long.MinValue;
        foreach (var m in _marks)
            if (m.start <= consumed && m.start >= best) { best = m.start; id = m.id; }
        return id;
    }
}
