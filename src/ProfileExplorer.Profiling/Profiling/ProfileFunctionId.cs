// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace ProfileExplorer.Core.Profile;

/// <summary>
/// Neutral, UI-agnostic identity for a profiled function: the (module, function-name) pair.
/// <para>
/// This mirrors the effective identity that <c>IRTextFunction</c> provides today
/// (interned name + owning module summary), so re-keying the profiling model from
/// <c>IRTextFunction</c> to <see cref="ProfileFunctionId"/> is behavior-preserving: functions
/// that share a name within a module are still treated as the same entity.
/// </para>
/// <para>
/// <c>FunctionDebugInfo</c> (RVA/size) is carried as payload on the model, not used as identity,
/// because its equality (RVA + size + id) would split same-name functions that the existing
/// pipeline merges.
/// </para>
/// </summary>
public readonly record struct ProfileFunctionId {
  private readonly string? moduleName_;
  private readonly string? functionName_;

  public ProfileFunctionId(string? moduleName, string? functionName) {
    moduleName_ = moduleName;
    functionName_ = functionName;
  }

  /// <summary>Owning module/image name (e.g., "ntdll.dll"). Never null; empty when unresolved.</summary>
  public string ModuleName => moduleName_ ?? string.Empty;

  /// <summary>Function name (as it appears in the module's symbols). Never null; empty when unresolved.</summary>
  public string FunctionName => functionName_ ?? string.Empty;

  /// <summary>True when this identity is empty/unresolved.</summary>
  public bool IsUnknown => string.IsNullOrEmpty(functionName_);

  public static ProfileFunctionId Unknown => default;

  // Treat null and empty names as the same identity so a `default` struct (which bypasses the
  // constructor) compares and hashes equal to an explicitly built empty id. The compiler-synthesized
  // record equality would otherwise compare the raw backing fields and split "unknown" frames across
  // separate null-keyed and empty-keyed buckets.
  public bool Equals(ProfileFunctionId other) {
    return ModuleName == other.ModuleName && FunctionName == other.FunctionName;
  }

  public override int GetHashCode() {
    return HashCode.Combine(ModuleName, FunctionName);
  }

  public override string ToString() => $"{ModuleName}!{FunctionName}";
}
