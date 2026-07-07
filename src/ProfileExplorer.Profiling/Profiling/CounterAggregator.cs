// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.Data;

namespace ProfileExplorer.Profiling.Profiling;

/// <summary>
/// Aggregates hardware performance counter events into per-function/instruction counter values,
/// keyed by neutral function identity.
/// </summary>
internal class CounterAggregator {
  private readonly IpResolver ipResolver_;
  private readonly Dictionary<ProfileFunctionId, Dictionary<long, PerformanceCounterValueSet>> countersByFunction_ = new();
  private readonly object lock_ = new();

  public CounterAggregator(IpResolver ipResolver) {
    ipResolver_ = ipResolver;
  }

  /// <summary>
  /// Add a batch of performance counter events.
  /// </summary>
  public void AddEvents(IEnumerable<IPerformanceCounterEvent> events) {
    foreach (var evt in events) {
      var resolved = ipResolver_.Resolve(evt.InstructionPointer);
      if (resolved?.FunctionName == null) continue;

      var id = new ProfileFunctionId(resolved.ModuleName, resolved.FunctionName);

      lock (lock_) {
        if (!countersByFunction_.TryGetValue(id, out var instrCounters)) {
          instrCounters = [];
          countersByFunction_[id] = instrCounters;
        }

        if (!instrCounters.TryGetValue(resolved.InstructionOffset, out var counterSet)) {
          counterSet = new PerformanceCounterValueSet();
          instrCounters[resolved.InstructionOffset] = counterSet;
        }

        counterSet.AddCounterSample(evt.CounterId, 1);
      }
    }
  }

  /// <summary>
  /// Get the per-instruction counter values for a specific function.
  /// </summary>
  public IReadOnlyDictionary<long, PerformanceCounterValueSet>? GetCounters(ProfileFunctionId functionId) {
    lock (lock_) {
      return countersByFunction_.TryGetValue(functionId, out var counters)
        ? new Dictionary<long, PerformanceCounterValueSet>(counters)
        : null;
    }
  }
}
