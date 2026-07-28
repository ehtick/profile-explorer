// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile;

namespace ProfileExplorer.Profiling;

/// <summary>
/// A single pre-resolved stack frame (leaf-first order) supplied by a host that owns symbol
/// resolution (e.g. Profile Explorer). Lets the library aggregate over already-resolved stacks
/// without re-resolving IPs, so host-side managed / JIT / unknown-frame resolution flows through
/// unchanged. Frames the host could not resolve should be omitted before aggregation.
/// </summary>
public readonly record struct ResolvedFrame(
  ProfileFunctionId FunctionId,
  FunctionDebugInfo DebugInfo,
  long FrameRva,
  bool IsKernel = false,
  bool IsManaged = false) {
  /// <summary>Instruction offset within the owning function (frame RVA minus function RVA).</summary>
  public long InstructionOffset => FrameRva - (DebugInfo?.RVA ?? 0);
}
