// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Concurrent;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.Data;

namespace ProfileExplorer.Profiling.Profiling;

/// <summary>
/// Aggregates CPU samples into per-function profiles keyed by neutral function identity.
/// Thread-safe — processes samples in parallel chunks.
/// </summary>
internal class SampleAggregator {
  private readonly IpResolver ipResolver_;
  private readonly ConcurrentDictionary<ProfileFunctionId, FunctionProfileData> functions_ = new();
  private TimeSpan totalWeight_;
  private readonly object totalWeightLock_ = new();

  public SampleAggregator(IpResolver ipResolver) {
    ipResolver_ = ipResolver;
  }

  /// <summary>
  /// Add a batch of samples. Thread-safe.
  /// </summary>
  public void AddSamples(IEnumerable<IProfileSample> samples) {
    TimeSpan batchWeight = TimeSpan.Zero;

    foreach (var sample in samples) {
      if (string.IsNullOrEmpty(sample.ImageName)) continue;

      var resolved = ipResolver_.Resolve(sample.InstructionPointer);
      if (resolved == null) continue;

      // Leaf frame: self (exclusive) + inclusive + per-instruction weight.
      var leaf = GetOrAddFunction(resolved);

      lock (leaf) {
        leaf.ExclusiveWeight += sample.Weight;
        leaf.Weight += sample.Weight;
        leaf.AddInstructionSample(resolved.InstructionOffset, sample.Weight);
      }

      batchWeight += sample.Weight;

      // Caller frames contribute inclusive weight only.
      // Stack is leaf-first; skip index 0 (leaf — already counted above).
      if (sample.StackFrames is { Count: > 1 }) {
        for (int i = 1; i < sample.StackFrames.Count; i++) {
          var callerResolved = ipResolver_.Resolve(sample.StackFrames[i]);
          if (callerResolved == null) continue;

          var caller = GetOrAddFunction(callerResolved);

          lock (caller) {
            caller.Weight += sample.Weight;
          }
        }
      }
    }

    lock (totalWeightLock_) {
      totalWeight_ += batchWeight;
    }
  }

  /// <summary>
  /// Build the final per-function profile map (snapshot).
  /// </summary>
  public Dictionary<ProfileFunctionId, FunctionProfileData> Build() {
    return new Dictionary<ProfileFunctionId, FunctionProfileData>(functions_);
  }

  public TimeSpan TotalWeight => totalWeight_;

  private FunctionProfileData GetOrAddFunction(ResolvedIp resolved) {
    string funcName = resolved.FunctionName ?? $"<unknown+0x{resolved.Rva:X}>";
    var id = new ProfileFunctionId(resolved.ModuleName, funcName);
    return functions_.GetOrAdd(id, _ => new FunctionProfileData(resolved.DebugInfo));
  }
}
