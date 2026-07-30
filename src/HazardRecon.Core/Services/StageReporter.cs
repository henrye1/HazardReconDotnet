using System.Diagnostics;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

/// <summary>
/// Tracks which step of a run is in flight and publishes the whole list on every
/// change.
///
/// Stages are planned before they run so the caller can show what is still to
/// come and say "step 3 of 11" honestly. Plans arrive in waves because the
/// per-set steps are only knowable once the folders have been read.
///
/// Reporting must never be able to fail a run: a throwing callback is swallowed,
/// and finishing or beginning an unplanned key is ignored rather than throwing.
/// </summary>
public sealed class StageReporter
{
    private readonly Action<IReadOnlyList<RunStage>>? _onChange;
    private readonly List<RunStage> _stages = new();
    private readonly Dictionary<string, Stopwatch> _timers = new();
    private readonly object _gate = new();

    public StageReporter(Action<IReadOnlyList<RunStage>>? onChange = null) => _onChange = onChange;

    /// <summary>Appends steps as pending. Keys already present are left alone.</summary>
    public void Plan(params (string Key, string Name, string Detail)[] steps)
    {
        lock (_gate)
        {
            foreach (var (key, name, detail) in steps)
            {
                if (_stages.Any(s => s.Key == key)) continue;
                _stages.Add(new RunStage { Key = key, Name = name, Detail = detail });
            }
        }
        Publish();
    }

    public void Begin(string key)
    {
        lock (_gate)
        {
            int i = _stages.FindIndex(s => s.Key == key);
            if (i < 0) return;
            _stages[i] = _stages[i] with { Status = StageStatus.Running, Seconds = null };
            _timers[key] = Stopwatch.StartNew();
        }
        Publish();
    }

    /// <summary>Closes a step off. Anything still running when the run ends is swept up by <see cref="Settle"/>.</summary>
    public void End(string key, string status = StageStatus.Done)
    {
        lock (_gate)
        {
            int i = _stages.FindIndex(s => s.Key == key);
            if (i < 0) return;

            // keep any duration already recorded: a stage may be closed twice, once
            // by Track and again to refine the status from its result
            double? secs = _stages[i].Seconds;
            if (_timers.Remove(key, out Stopwatch? sw))
            {
                sw.Stop();
                secs = Math.Round(sw.Elapsed.TotalSeconds, 1);
            }

            _stages[i] = _stages[i] with { Status = status, Seconds = secs };
        }
        Publish();
    }

    /// <summary>
    /// Runs a step, timing it. A throwing body marks the step failed and rethrows,
    /// so a crashed run does not leave a row spinning forever.
    /// </summary>
    public T Track<T>(string key, Func<T> body)
    {
        Begin(key);
        try
        {
            T result = body();
            End(key);
            return result;
        }
        catch
        {
            End(key, StageStatus.Error);
            throw;
        }
    }

    public void Track(string key, Action body) => Track(key, () => { body(); return 0; });

    /// <summary>
    /// Marks whatever is left as finished, for the end of a run. Running steps
    /// become <paramref name="runningBecomes"/>; untouched pending steps are
    /// skipped, since the run got past them without doing the work.
    /// </summary>
    public void Settle(string runningBecomes = StageStatus.Error)
    {
        lock (_gate)
        {
            for (int i = 0; i < _stages.Count; i++)
            {
                if (_stages[i].Status == StageStatus.Running)
                {
                    double? secs = null;
                    if (_timers.Remove(_stages[i].Key, out Stopwatch? sw))
                    {
                        sw.Stop();
                        secs = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    }
                    _stages[i] = _stages[i] with { Status = runningBecomes, Seconds = secs };
                }
                else if (_stages[i].Status == StageStatus.Pending)
                {
                    _stages[i] = _stages[i] with { Status = StageStatus.Skipped };
                }
            }
        }
        Publish();
    }

    public IReadOnlyList<RunStage> Snapshot()
    {
        lock (_gate) return _stages.ToList();
    }

    private void Publish()
    {
        if (_onChange == null) return;
        try { _onChange(Snapshot()); }
        catch { /* progress reporting may never break a run */ }
    }
}
