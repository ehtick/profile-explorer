// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Concurrent;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.Data;

namespace ProfileExplorer.Profiling.Profiling;

/// <summary>
/// Aggregates CPU samples into per-function profiles keyed by neutral function identity.
/// The per-function <see cref="FunctionProfileData"/> entries live in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and each is mutated under its own lock, so multiple <see cref="AddSamples"/> calls may run
/// concurrently from different threads. A single <see cref="AddSamples"/> call processes its batch
/// sequentially.
/// </summary>
internal class SampleAggregator {
  private readonly IpResolver ipResolver_;
  private readonly ConcurrentDictionary<ProfileFunctionId, FunctionProfileData> functions_ = new();
  private TimeSpan totalWeight_;
  private readonly object totalWeightLock_ = new();

  // Per-thread scratch set reused across AddResolvedStack calls (each parallel chunk thread gets its
  // own) so recursive functions are credited inclusive time once per stack without per-call alloc.
  [ThreadStatic] private static HashSet<ProfileFunctionId>? resolvedCredited_;

  public SampleAggregator(IpResolver ipResolver) {
    ipResolver_ = ipResolver;
  }

  /// <summary>
  /// Add a batch of samples. Thread-safe.
  /// </summary>
  public void AddSamples(IEnumerable<IProfileSample> samples,
                         IReadOnlyList<ProfileFunctionId>? instancePath = null) {
    TimeSpan batchWeight = TimeSpan.Zero;

    // Reused across samples to track which functions already received inclusive weight for the
    // current stack, so recursive functions (appearing multiple times on one stack) are only
    // credited inclusive time once per sample.
    var creditedThisStack = new HashSet<ProfileFunctionId>();

    foreach (var sample in samples) {
      if (string.IsNullOrEmpty(sample.ImageName)) continue;

      // Call-tree instance filter: include only samples whose stack (read from the root) begins with
      // the instance path. Matches Core FunctionProfileProcessor's instance-path prefix filtering.
      if (instancePath is { Count: > 0 } &&
          !ipResolver_.StackHasInstancePrefix(sample.StackFrames, instancePath)) {
        continue;
      }

      var resolved = ipResolver_.Resolve(sample.InstructionPointer);
      if (resolved == null) continue;

      creditedThisStack.Clear();

      // Leaf frame: self (exclusive) + inclusive + per-instruction weight.
      var leaf = GetOrAddFunction(resolved, out var leafId);
      creditedThisStack.Add(leafId);

      lock (leaf) {
        leaf.ExclusiveWeight += sample.Weight;
        leaf.Weight += sample.Weight;
        leaf.AddInstructionSample(resolved.InstructionOffset, sample.Weight);
      }

      batchWeight += sample.Weight;

      // Caller frames contribute inclusive weight plus a per-instruction sample at the call-site
      // (the return address offset within the caller). This mirrors Core's
      // FunctionProfileProcessor.ProcessSample, which credits AddInstructionSample for every unique
      // function on the stack — so a `call` instruction shows the inclusive time of what it invoked.
      // Stack is leaf-first; skip index 0 (leaf — already counted above).
      if (sample.StackFrames is { Count: > 1 }) {
        for (int i = 1; i < sample.StackFrames.Count; i++) {
          var callerResolved = ipResolver_.Resolve(sample.StackFrames[i]);
          if (callerResolved == null) continue;

          var caller = GetOrAddFunction(callerResolved, out var callerId);

          // Skip recursive re-entry of a function already credited inclusive time on this stack.
          if (!creditedThisStack.Add(callerId)) continue;

          lock (caller) {
            caller.Weight += sample.Weight;
            caller.AddInstructionSample(callerResolved.InstructionOffset, sample.Weight);
          }
        }
      }
    }

    lock (totalWeightLock_) {
      totalWeight_ += batchWeight;
    }
  }

  /// <summary>
  /// Aggregate one PRE-RESOLVED sample stack (leaf-first). Frames already carry their function
  /// identity and offset, so no IP resolution happens — used by hosts (e.g. Profile Explorer) that
  /// own symbol resolution. Mirrors the per-frame exclusive/inclusive/instruction math of the raw
  /// <see cref="AddSamples"/> path and of Core's FunctionProfileProcessor. Thread-safe.
  /// </summary>
  public void AddResolvedStack(TimeSpan weight, IReadOnlyList<ResolvedFrame> framesLeafFirst,
                               IReadOnlyList<ProfileFunctionId>? instancePath = null) {
    if (framesLeafFirst.Count == 0) return;

    if (instancePath is { Count: > 0 } &&
        !IpResolver.StackHasInstancePrefixResolved(framesLeafFirst, instancePath)) {
      return;
    }

    var credited = resolvedCredited_ ??= new HashSet<ProfileFunctionId>();
    credited.Clear();
    bool isTop = true;

    for (int i = 0; i < framesLeafFirst.Count; i++) {
      var frame = framesLeafFirst[i];
      var id = frame.FunctionId;
      var fp = functions_.GetOrAdd(id, static (_, dbg) => new FunctionProfileData(dbg), frame.DebugInfo);

      lock (fp) {
        if (credited.Add(id)) {
          fp.Weight += weight;
          fp.AddInstructionSample(frame.InstructionOffset, weight);
        }

        if (isTop) {
          fp.ExclusiveWeight += weight;
        }
      }

      isTop = false;
    }

    lock (totalWeightLock_) {
      totalWeight_ += weight;
    }
  }

  /// <summary>
  /// Build the final per-function profile map (snapshot).
  /// </summary>
  public Dictionary<ProfileFunctionId, FunctionProfileData> Build() {
    return new Dictionary<ProfileFunctionId, FunctionProfileData>(functions_);
  }

  public TimeSpan TotalWeight => totalWeight_;

  private FunctionProfileData GetOrAddFunction(ResolvedIp resolved, out ProfileFunctionId id) {
    string funcName = resolved.FunctionName ?? $"<unknown+0x{resolved.Rva:X}>";
    id = new ProfileFunctionId(resolved.ModuleName, funcName);
    return functions_.GetOrAdd(id, _ => new FunctionProfileData(resolved.DebugInfo));
  }
}
