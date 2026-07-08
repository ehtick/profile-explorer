// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.CallTree;

namespace ProfileExplorer.Profiling.Profiling;

/// <summary>
/// Builds a mature <see cref="ProfileCallTree"/> from resolved stack samples.
/// Stacks are expected to be leaf-first (index 0 = leaf, last index = root).
/// </summary>
internal class CallTreeBuilder {
  private ProfileCallTree callTree_ = new();
  private readonly IpResolver ipResolver_;
  private readonly object lock_ = new();

  public CallTreeBuilder(IpResolver ipResolver) {
    ipResolver_ = ipResolver;
  }

  /// <summary>
  /// Add samples with stack frames to the call tree.
  /// Stacks are expected to be leaf-first (index 0 = leaf, last index = root).
  /// </summary>
  public void AddSamples(IEnumerable<IProfileSample> samples,
                         IReadOnlyList<ProfileFunctionId>? instancePath = null) {
    foreach (var sample in samples) {
      if (sample.StackFrames is not { Count: > 0 }) continue;

      // Call-tree instance filter: include only samples whose stack begins (from the root) with the
      // instance path, so the focused call tree matches the focused per-function profile.
      if (instancePath is { Count: > 0 } &&
          !ipResolver_.StackHasInstancePrefix(sample.StackFrames, instancePath)) {
        continue;
      }

      // Resolve frames, preserving leaf-first order expected by ProfileCallTree.UpdateCallTree.
      var frames = new List<ResolvedCallStackFrame>(sample.StackFrames.Count);

      foreach (long ip in sample.StackFrames) {
        var resolved = ipResolver_.Resolve(ip);
        if (resolved == null) continue;

        string funcName = resolved.FunctionName ?? $"<unknown+0x{resolved.Rva:X}>";
        var id = new ProfileFunctionId(resolved.ModuleName, funcName);
        frames.Add(new ResolvedCallStackFrame(resolved.Rva + resolved.InstructionOffset, resolved.DebugInfo, id,
                                              isKernelCode: false, resolved.IsManaged));
      }

      if (frames.Count == 0) continue;

      var stack = new ResolvedSampleStack(frames, sample.ThreadId);

      lock (lock_) {
        callTree_.UpdateCallTree(sample.Weight, stack);
      }
    }
  }

  /// <summary>
  /// Add one PRE-RESOLVED sample stack (leaf-first) to the call tree. Frames already carry their
  /// resolved identity, so no IP resolution happens. Thread-safe.
  /// </summary>
  public void AddResolvedStack(TimeSpan weight, int threadId, IReadOnlyList<ResolvedFrame> framesLeafFirst,
                               IReadOnlyList<ProfileFunctionId>? instancePath = null) {
    if (framesLeafFirst.Count == 0) return;

    if (instancePath is { Count: > 0 } &&
        !IpResolver.StackHasInstancePrefixResolved(framesLeafFirst, instancePath)) {
      return;
    }

    var stack = new ResolvedSampleStack(BuildFrames(framesLeafFirst), threadId);

    lock (lock_) {
      callTree_.UpdateCallTree(weight, stack);
    }
  }

  /// <summary>
  /// Create an isolated call tree for a single parallel chunk. Its node-ID namespace is partitioned
  /// by <paramref name="chunkIndex"/> so the per-chunk trees can be merged later without ID
  /// collisions (same scheme as the former Core CallTreeProcessor).
  /// </summary>
  public ProfileCallTree CreateChunkTree(int chunkIndex, int chunkCount) {
    int startNodeId = chunkIndex * (int.MaxValue / (chunkCount + 1));
    return new ProfileCallTree(startNodeId);
  }

  /// <summary>
  /// Add one PRE-RESOLVED stack (leaf-first) to a chunk-local tree. No locking ΓÇö each chunk tree is
  /// owned by a single worker thread; call <see cref="MergeChunkTrees"/> once all workers finish.
  /// </summary>
  public void AddResolvedStackToChunk(ProfileCallTree chunkTree, TimeSpan weight, int threadId,
                                      IReadOnlyList<ResolvedFrame> framesLeafFirst) {
    if (framesLeafFirst.Count == 0) return;
    var stack = new ResolvedSampleStack(BuildFrames(framesLeafFirst), threadId);
    chunkTree.UpdateCallTree(weight, stack);
  }

  /// <summary>
  /// Merge the per-chunk trees produced by parallel workers into this builder's tree, using a
  /// log-depth parallel pairwise merge (faithful to the former CallTreeProcessor.Complete). After
  /// this returns, <see cref="Build"/> yields the merged tree.
  /// </summary>
  public void MergeChunkTrees(IReadOnlyList<ProfileCallTree> chunkTrees) {
    if (chunkTrees.Count == 0) return;

    var chunks = new List<ProfileCallTree>(chunkTrees);

    while (chunks.Count > 1) {
      const int step = 2;
      int pairCount = chunks.Count / step;
      var tasks = new Task[pairCount];
      var newChunks = new List<ProfileCallTree>(pairCount + 1);

      for (int i = 0; i < pairCount; i++) {
        int baseIndex = i * step;
        newChunks.Add(chunks[baseIndex]);
        tasks[i] = Task.Run(() => chunks[baseIndex].MergeWith(chunks[baseIndex + 1]));
      }

      Task.WaitAll(tasks);

      // Odd trailing tree (only possible in the first round with step 2): fold into the first.
      for (int i = pairCount * step; i < chunks.Count; i++) {
        chunks[0].MergeWith(chunks[i]);
      }

      chunks = newChunks;
    }

    callTree_ = chunks[0];
#if DEBUG
    callTree_.VerifyCycles();
#endif
  }

  /// <summary>
  /// Returns the built call tree (a forest of root nodes).
  /// </summary>
  public ProfileCallTree Build() {
    return callTree_;
  }

  // Project neutral resolved frames (leaf-first) into the call-tree frame type.
  private static List<ResolvedCallStackFrame> BuildFrames(IReadOnlyList<ResolvedFrame> framesLeafFirst) {
    var frames = new List<ResolvedCallStackFrame>(framesLeafFirst.Count);

    for (int i = 0; i < framesLeafFirst.Count; i++) {
      var f = framesLeafFirst[i];
      frames.Add(new ResolvedCallStackFrame(f.FrameRva, f.DebugInfo, f.FunctionId, f.IsKernel, f.IsManaged));
    }

    return frames;
  }

  // Adapter exposing resolved frames (leaf-first) to ProfileCallTree.UpdateCallTree.
  private sealed class ResolvedSampleStack : IResolvedCallStack {
    private readonly List<ResolvedCallStackFrame> frames_;

    public ResolvedSampleStack(List<ResolvedCallStackFrame> frames, int threadId) {
      frames_ = frames;
      ThreadId = threadId;
    }

    public int FrameCount => frames_.Count;
    public int ThreadId { get; }

    public ResolvedCallStackFrame GetFrame(int index) {
      return frames_[index];
    }
  }
}
