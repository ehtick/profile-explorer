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
  private readonly ProfileCallTree callTree_ = new();
  private readonly IpResolver ipResolver_;
  private readonly object lock_ = new();

  public CallTreeBuilder(IpResolver ipResolver) {
    ipResolver_ = ipResolver;
  }

  /// <summary>
  /// Add samples with stack frames to the call tree.
  /// Stacks are expected to be leaf-first (index 0 = leaf, last index = root).
  /// </summary>
  public void AddSamples(IEnumerable<IProfileSample> samples) {
    foreach (var sample in samples) {
      if (sample.StackFrames is not { Count: > 0 }) continue;

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
  /// Returns the built call tree (a forest of root nodes).
  /// </summary>
  public ProfileCallTree Build() {
    return callTree_;
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
