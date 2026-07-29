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

  // Single synthetic bucket for user-space imageless (JITted/unmapped) leaf samples, mirroring Core's
  // "[Unknown Module]" so their CPU time is counted in the total (denominator) and surfaced as one
  // line, rather than silently dropped (which would inflate every resolved function's percentage).
  private static readonly ProfileFunctionId UnknownFunctionId = new("[Unknown Module]", "(unknown)");

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
      // Call-tree instance filter: include only samples whose stack (read from the root) begins with
      // the instance path. Matches Core FunctionProfileProcessor's instance-path prefix filtering.
      // This is the ONLY filter that removes a sample from the total denominator.
      if (instancePath is { Count: > 0 } &&
          !ipResolver_.StackHasInstancePrefix(sample.StackFrames, instancePath)) {
        continue;
      }

      // A sample whose leaf IP is outside every loaded module (imageless — e.g. JITted/unmapped code).
      // Match Core (ETWProfileDataProvider.ProcessUnresolvedStackAsync):
      //   * kernel-space imageless leaf => dropped (Core adds a null frame, credited to no function);
      //   * user-space imageless leaf   => credited to a single synthetic "(unknown)" bucket, so the
      //     CPU time is counted in the total (denominator) and surfaced instead of hidden. (Core keys
      //     this per-thread to keep its call tree from self-recursing; the flat report needs only one
      //     bucket to reproduce Core's Sum(ExclusiveWeight) total.)
      if (string.IsNullOrEmpty(sample.ImageName)) {
        if (ProfileAddress.IsKernelAddress((ulong)sample.InstructionPointer, pointerSize: 8)) {
          continue;
        }

        batchWeight += sample.Weight;
        var unknown = functions_.GetOrAdd(UnknownFunctionId, static _ => new FunctionProfileData());

        lock (unknown) {
          unknown.ExclusiveWeight += sample.Weight;
          unknown.Weight += sample.Weight;
        }

        continue;
      }

      // A sample whose leaf lies in a KNOWN image but resolves to no function still counts toward the
      // total denominator: Core creates a hex-named placeholder function for such an address, so its
      // self weight is included in Sum(ExclusiveWeight). Dropping it from the denominator would inflate
      // every resolved function's percentage. It is counted here but attributed to no named function.
      batchWeight += sample.Weight;

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

    AggregateFrames(weight, framesLeafFirst);

    lock (totalWeightLock_) {
      totalWeight_ += weight;
    }
  }

  /// <summary>
  /// Aggregate one PRE-RESOLVED, PRE-FILTERED stack's frames into the shared per-function map
  /// WITHOUT updating <see cref="TotalWeight"/>. Used by the parallel driver
  /// (<see cref="FunctionProfiler.AddResolvedSamplesParallel"/>), which batches the total per worker
  /// via <see cref="AddTotalWeight"/> so the global total lock is taken once per worker instead of
  /// once per sample. Thread-safe (per-function locks). Instance filtering is the caller's job.
  /// </summary>
  public void AggregateResolvedStack(TimeSpan weight, IReadOnlyList<ResolvedFrame> framesLeafFirst) {
    if (framesLeafFirst.Count == 0) return;
    AggregateFrames(weight, framesLeafFirst);
  }

  /// <summary>
  /// Add a batch of weight to <see cref="TotalWeight"/> under a single lock. The parallel driver
  /// accumulates a per-worker total (including passed-filter samples whose stack fully failed to
  /// resolve — they contribute to the total denominator but to no function, matching Core's
  /// FunctionProfileProcessor) and calls this once per worker.
  /// </summary>
  public void AddTotalWeight(TimeSpan weight) {
    if (weight == TimeSpan.Zero) return;

    lock (totalWeightLock_) {
      totalWeight_ += weight;
    }
  }

  // Aggregate a leaf-first resolved stack's frames into the shared per-function map: leaf frame gets
  // exclusive weight; every unique function on the stack gets inclusive weight + a per-instruction
  // sample (recursion credited once). Does not touch TotalWeight. Thread-safe (per-function locks).
  private void AggregateFrames(TimeSpan weight, IReadOnlyList<ResolvedFrame> framesLeafFirst) {
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
