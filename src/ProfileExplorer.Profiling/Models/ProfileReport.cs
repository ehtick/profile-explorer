// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.CallTree;
using ProfileExplorer.Core.Profile.Data;

namespace ProfileExplorer.Profiling;

/// <summary>
/// Aggregated result of a profiling run: the per-function profile map, the call tree, and the
/// total sampled weight. Functions are keyed by their neutral <see cref="ProfileFunctionId"/>
/// (module + function name); per-function detail lives on <see cref="FunctionProfileData"/>.
/// </summary>
public sealed class ProfileReport {
  public ProfileReport(IReadOnlyDictionary<ProfileFunctionId, FunctionProfileData> functions,
                       ProfileCallTree callTree, TimeSpan totalWeight) {
    Functions = functions;
    CallTree = callTree;
    TotalWeight = totalWeight;
  }

  /// <summary>Per-function aggregated profile, keyed by neutral function identity.</summary>
  public IReadOnlyDictionary<ProfileFunctionId, FunctionProfileData> Functions { get; }

  /// <summary>Call tree built from the sample stacks.</summary>
  public ProfileCallTree CallTree { get; }

  /// <summary>Total weight (CPU time) across all samples.</summary>
  public TimeSpan TotalWeight { get; }

  /// <summary>Returns <paramref name="weight"/> as a fraction (0..1) of the total sampled weight.</summary>
  public double ScaleWeight(TimeSpan weight) {
    return TotalWeight.Ticks > 0 ? weight.Ticks / (double)TotalWeight.Ticks : 0.0;
  }

  /// <summary>Functions sorted by self (exclusive) weight, descending.</summary>
  public List<KeyValuePair<ProfileFunctionId, FunctionProfileData>> FunctionsBySelfWeight() {
    var list = new List<KeyValuePair<ProfileFunctionId, FunctionProfileData>>(Functions);
    list.Sort((a, b) => b.Value.ExclusiveWeight.CompareTo(a.Value.ExclusiveWeight));
    return list;
  }

  /// <summary>Functions sorted by total (inclusive) weight, descending.</summary>
  public List<KeyValuePair<ProfileFunctionId, FunctionProfileData>> FunctionsByTotalWeight() {
    var list = new List<KeyValuePair<ProfileFunctionId, FunctionProfileData>>(Functions);
    list.Sort((a, b) => b.Value.Weight.CompareTo(a.Value.Weight));
    return list;
  }
}
