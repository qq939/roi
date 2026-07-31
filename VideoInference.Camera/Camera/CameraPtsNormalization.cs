using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VideoInferenceDemo;

public sealed class MonotonicPtsClock
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastPtsMs = -1;

    public long Next()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now < _lastPtsMs)
        {
            now = _lastPtsMs;
        }

        _lastPtsMs = now;
        return now;
    }
}

public sealed class DeviceTimestampPtsNormalizer
{
    private static readonly double[] CandidateRawUnitsPerMillisecond =
    {
        1,
        90,
        1_000,
        10_000,
        100_000,
        125_000,
        1_000_000
    };

    private readonly double _expectedFrameIntervalMs;
    private readonly List<long> _observedDeltas = new();
    private long? _firstRawTimestamp;
    private long _lastRawTimestamp;
    private long _lastPtsMs;
    private double? _rawUnitsPerMillisecond;

    public DeviceTimestampPtsNormalizer(double expectedFps)
    {
        _expectedFrameIntervalMs = expectedFps > 0 ? 1000.0 / expectedFps : 0.0;
    }

    public bool TryNormalize(long rawTimestamp, out long ptsMs)
    {
        ptsMs = 0;
        if (rawTimestamp <= 0)
        {
            return false;
        }

        if (!_firstRawTimestamp.HasValue)
        {
            _firstRawTimestamp = rawTimestamp;
            _lastRawTimestamp = rawTimestamp;
            _lastPtsMs = 0;
            ptsMs = 0;
            return true;
        }

        var rawDelta = rawTimestamp - _lastRawTimestamp;
        if (rawDelta > 0)
        {
            _observedDeltas.Add(rawDelta);
            if (_observedDeltas.Count > 16)
            {
                _observedDeltas.RemoveAt(0);
            }
        }

        _lastRawTimestamp = rawTimestamp;
        _rawUnitsPerMillisecond ??= EstimateScale();
        if (!_rawUnitsPerMillisecond.HasValue || _rawUnitsPerMillisecond.Value <= 0)
        {
            return false;
        }

        var normalized = (long)Math.Round((rawTimestamp - _firstRawTimestamp.Value) / _rawUnitsPerMillisecond.Value);
        if (normalized < _lastPtsMs)
        {
            normalized = _lastPtsMs;
        }

        _lastPtsMs = normalized;
        ptsMs = normalized;
        return true;
    }

    private double? EstimateScale()
    {
        if (_observedDeltas.Count == 0)
        {
            return CandidateRawUnitsPerMillisecond[0];
        }

        var averageDelta = 0.0;
        foreach (var delta in _observedDeltas)
        {
            averageDelta += delta;
        }

        averageDelta /= _observedDeltas.Count;
        if (averageDelta <= 0)
        {
            return null;
        }

        var bestScale = CandidateRawUnitsPerMillisecond[0];
        var bestScore = double.MaxValue;
        foreach (var candidate in CandidateRawUnitsPerMillisecond)
        {
            var candidateDeltaMs = averageDelta / candidate;
            var score = _expectedFrameIntervalMs > 0
                ? Math.Abs(candidateDeltaMs - _expectedFrameIntervalMs)
                : Math.Abs(candidateDeltaMs - 33.333);

            if (score < bestScore)
            {
                bestScale = candidate;
                bestScore = score;
            }
        }

        return bestScale;
    }
}

public sealed class TimestampDeltaPtsNormalizer
{
    private long? _firstRawTimestamp;
    private long _lastPtsMs;

    public bool TryNormalize(long rawTimestampMs, out long ptsMs)
    {
        ptsMs = 0;
        if (rawTimestampMs <= 0)
        {
            return false;
        }

        if (!_firstRawTimestamp.HasValue)
        {
            _firstRawTimestamp = rawTimestampMs;
            _lastPtsMs = 0;
            ptsMs = 0;
            return true;
        }

        var normalized = rawTimestampMs - _firstRawTimestamp.Value;
        if (normalized < _lastPtsMs)
        {
            normalized = _lastPtsMs;
        }

        _lastPtsMs = normalized;
        ptsMs = normalized;
        return true;
    }
}
