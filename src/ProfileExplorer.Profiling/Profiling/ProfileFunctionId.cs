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
  public ProfileFunctionId(string moduleName, string functionName) {
    ModuleName = moduleName ?? string.Empty;
    FunctionName = functionName ?? string.Empty;
  }

  /// <summary>Owning module/image name (e.g., "ntdll.dll").</summary>
  public string ModuleName { get; }

  /// <summary>Function name (as it appears in the module's symbols).</summary>
  public string FunctionName { get; }

  /// <summary>True when this identity is empty/unresolved.</summary>
  public bool IsUnknown => string.IsNullOrEmpty(FunctionName);

  public static ProfileFunctionId Unknown => default;

  public override string ToString() => $"{ModuleName}!{FunctionName}";
}
