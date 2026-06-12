// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Binary;

namespace ProfileExplorer.Core.Profile.CallTree;

/// <summary>
/// Neutral, resolved CPU call stack consumed by <see cref="ProfileCallTree.UpdateCallTree"/>.
/// Frames are indexed leaf-first (index 0 = innermost/leaf frame); the call-tree build walks
/// them in reverse to form root-to-leaf paths. Implemented by the trace provider (e.g.
/// ProfileExplorerCore's ResolvedProfileStack) so the call-tree build logic stays free of any
/// trace-format or IR-specific types.
/// </summary>
public interface IResolvedCallStack {
  /// <summary>Number of frames in the stack.</summary>
  int FrameCount { get; }

  /// <summary>Thread that produced the sample.</summary>
  int ThreadId { get; }

  /// <summary>Returns the resolved frame at the given index (0 = leaf).</summary>
  ResolvedCallStackFrame GetFrame(int index);
}

/// <summary>
/// A single resolved stack frame: neutral function identity plus the data the call-tree build
/// needs. A lightweight value type to avoid per-sample heap allocations.
/// </summary>
public readonly struct ResolvedCallStackFrame {
  public ResolvedCallStackFrame(long frameRva, FunctionDebugInfo debugInfo, ProfileFunctionId functionId,
                                bool isKernelCode, bool isManagedCode) {
    FrameRva = frameRva;
    DebugInfo = debugInfo;
    FunctionId = functionId;
    IsKernelCode = isKernelCode;
    IsManagedCode = isManagedCode;
  }

  public long FrameRva { get; }
  public FunctionDebugInfo DebugInfo { get; }
  public ProfileFunctionId FunctionId { get; }
  public bool IsKernelCode { get; }
  public bool IsManagedCode { get; }
}
