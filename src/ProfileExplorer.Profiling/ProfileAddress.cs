// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace ProfileExplorer.Profiling;

/// <summary>
/// Address helpers shared across the profiling engine, Profile Explorer Core, and hosts (e.g. the
/// TraceEvent-based extractors) so the kernel/user address threshold is defined in exactly ONE place
/// and can never drift between them. Core's <c>ETWEventProcessor.IsKernelAddress</c> forwards here.
/// </summary>
public static class ProfileAddress {
  /// <summary>
  /// True when <paramref name="ip"/> is a kernel-space address for the given pointer size:
  /// 32-bit (<paramref name="pointerSize"/> == 4) uses the <c>0x80000000</c> split; 64-bit uses the
  /// canonical upper half (<c>&gt;= 0xFFFF000000000000</c>).
  /// </summary>
  public static bool IsKernelAddress(ulong ip, int pointerSize) {
    if (pointerSize == 4) {
      return ip >= 0x80000000;
    }

    return ip >= 0xFFFF000000000000;
  }
}
