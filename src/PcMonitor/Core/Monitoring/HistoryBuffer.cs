namespace PcMonitor.Core;

/// <summary>Кольцевой буфер последних значений датчика для графика.</summary>
public sealed class HistoryBuffer
{
    private readonly float[] _data;
    private int _head;
    private int _count;

    public HistoryBuffer(int capacity)
    {
        if (capacity < 2) capacity = 2;
        _data = new float[capacity];
    }

    public int Capacity => _data.Length;

    public int Count => _count;

    public void Add(float value)
    {
        _data[_head] = value;
        _head = (_head + 1) % _data.Length;
        if (_count < _data.Length) _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    /// <summary>Копия значений в хронологическом порядке.</summary>
    public float[] ToArray()
    {
        var result = new float[_count];
        int start = _count == _data.Length ? _head : 0;
        for (int i = 0; i < _count; i++)
            result[i] = _data[(start + i) % _data.Length];
        return result;
    }
}
