namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Maps tracker order/row positions back to their first-pass media time after an authored loop.
/// </summary>
internal sealed class TrackerPositionTimeline
{
    private readonly Lock _sync = new();
    private readonly Dictionary<int, List<RowSample>> _samplesByOrder = [];
    private int _lastOrder = -1;
    private int _lastRow = -1;
    private bool _loopDetected;

    public double Resolve(int order, int row, double bytePositionSeconds)
    {
        lock (_sync)
        {
            if (_lastOrder >= 0 && IsBefore(order, row, _lastOrder, _lastRow))
                _loopDetected = true;

            _lastOrder = order;
            _lastRow = row;

            if (!_loopDetected)
            {
                AddSample(order, row, bytePositionSeconds);
                return bytePositionSeconds;
            }

            return ResolveMappedPosition(order, row, bytePositionSeconds);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetCore();
        }
    }

    private void AddSample(int order, int row, double seconds)
    {
        if (!_samplesByOrder.TryGetValue(order, out var samples))
        {
            samples = [];
            _samplesByOrder.Add(order, samples);
        }

        if (samples.Count == 0 || samples[^1].Row < row)
        {
            samples.Add(new RowSample(row, seconds));
        }
    }

    private double ResolveMappedPosition(int order, int row, double fallbackSeconds)
    {
        if (!_samplesByOrder.TryGetValue(order, out var samples) || samples.Count == 0)
        {
            return fallbackSeconds;
        }

        int upperIndex = samples.FindIndex(sample => sample.Row >= row);
        if (upperIndex < 0)
        {
            return samples[^1].Seconds;
        }

        RowSample upper = samples[upperIndex];
        if (upper.Row == row || upperIndex == 0)
        {
            return upper.Seconds;
        }

        RowSample lower = samples[upperIndex - 1];
        double rowRange = upper.Row - lower.Row;
        double progress = rowRange > 0 ? (row - lower.Row) / rowRange : 0;
        return lower.Seconds + ((upper.Seconds - lower.Seconds) * progress);
    }

    private void ResetCore()
    {
        _samplesByOrder.Clear();
        _lastOrder = -1;
        _lastRow = -1;
        _loopDetected = false;
    }

    private static bool IsBefore(int order, int row, int previousOrder, int previousRow) =>
        order < previousOrder || (order == previousOrder && row < previousRow);

    private readonly record struct RowSample(int Row, double Seconds);
}
