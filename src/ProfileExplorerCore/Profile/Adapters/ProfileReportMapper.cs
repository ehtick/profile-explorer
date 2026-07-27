// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using ProfileExplorer.Core.IR;
using ProfileExplorer.Core.Profile.Data;
using ProfileExplorer.Profiling;

namespace ProfileExplorer.Core.Profile.Adapters;

/// <summary>
/// Projects a library <see cref="ProfileReport"/> (the neutral output of the ProfileExplorer.Profiling
/// engine) onto Core's <see cref="ProfileData"/> shape used by the UI. This is the return leg of the
/// engine deduplication: the library owns aggregation and produces per-function profiles keyed by
/// <see cref="ProfileFunctionId"/>; Core supplies the <see cref="IRTextFunction"/> mapping (for document
/// navigation) and module identity (for per-module weight display).
/// </summary>
public static class ProfileReportMapper {
  /// <summary>
  /// Populate <paramref name="target"/> from <paramref name="report"/>.
  /// </summary>
  /// <param name="target">The ProfileData to fill (typically a freshly constructed instance).</param>
  /// <param name="report">The library aggregation result.</param>
  /// <param name="resolveFunction">
  /// Maps a neutral function identity to its <see cref="IRTextFunction"/> (Core owns the per-module
  /// IRTextSummary registry). May return <c>null</c> for functions with no IR representation
  /// (e.g. unresolved/JIT frames), in which case no resolver entry is added.
  /// </param>
  /// <param name="moduleIdByName">
  /// Maps a module name to the representative <see cref="ProfileImage.Id"/> used to key
  /// <see cref="ProfileData.ModuleWeights"/>. Module exclusive weight is summed from the per-function
  /// exclusive weights (the leaf-attributed time), consistent with Core's module-weight semantics.
  /// </param>
  public static void ApplyReport(ProfileData target, ProfileReport report,
                                 Func<ProfileFunctionId, IRTextFunction?> resolveFunction,
                                 Func<string, int> moduleIdByName) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(report);
    ArgumentNullException.ThrowIfNull(resolveFunction);
    ArgumentNullException.ThrowIfNull(moduleIdByName);

    var functions = new Dictionary<ProfileFunctionId, FunctionProfileData>(report.Functions.Count);
    var resolver = new Dictionary<ProfileFunctionId, IRTextFunction>(report.Functions.Count);
    var moduleWeights = new Dictionary<int, TimeSpan>();

    foreach (var pair in report.Functions) {
      var id = pair.Key;
      var data = pair.Value;
      functions[id] = data;

      var irFunction = resolveFunction(id);

      if (irFunction != null) {
        resolver[id] = irFunction;
      }

      // Module time is the leaf-attributed (exclusive) time, aggregated per module — matching Core's
      // FunctionProfileProcessor, which accumulates module weight from the top (leaf) stack frame.
      int moduleId = moduleIdByName(id.ModuleName);
      moduleWeights.TryGetValue(moduleId, out var existing);
      moduleWeights[moduleId] = existing + data.ExclusiveWeight;
    }

    target.FunctionProfiles = functions;
    target.FunctionResolver = resolver;
    target.ModuleWeights = moduleWeights;
    target.CallTree = report.CallTree;
    target.TotalWeight = report.TotalWeight;
    target.ProfileWeight = report.TotalWeight;
  }
}
