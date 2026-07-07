// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace ProfileExplorer.Core.Profile;

/// <summary>
/// Helpers to derive the neutral <see cref="ProfileFunctionId"/> from an <c>IRTextFunction</c>.
/// Kept on the PE side because <see cref="ProfileFunctionId"/> itself is IRTextFunction-free
/// (so it can live in the library alongside the migrated profiling model).
/// </summary>
public static class ProfileFunctionIdExtensions {
  public static ProfileFunctionId ToProfileId(this IRTextFunction function) {
    return function != null ? new ProfileFunctionId(function.ModuleName, function.Name) : default;
  }
}
