// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.Adapters;
using ProfileExplorer.Core.Profile.CallTree;
using ProfileExplorer.Core.Profile.Data;
using ProfileExplorer.Profiling;

namespace ProfileExplorer.CoreTests;

/// <summary>
/// Verifies the return-leg mapping of the engine deduplication: a library <see cref="ProfileReport"/>
/// projected onto Core's <see cref="ProfileData"/> (function profiles, IRTextFunction resolver,
/// per-module weights, call tree, and totals).
/// </summary>
[TestClass]
public class ProfileReportMapperTests {
  private const string Module = "M.dll";
  private const int ModuleId = 42;

  [TestMethod]
  public void ApplyReport_PopulatesProfiles_Resolver_ModuleWeights_Totals() {
    var idA = new ProfileFunctionId(Module, "A");
    var idB = new ProfileFunctionId(Module, "B");

    var dataA = new FunctionProfileData(new FunctionDebugInfo("A", 0x1000, 0x100)) {
      ExclusiveWeight = TimeSpan.FromMilliseconds(10), Weight = TimeSpan.FromMilliseconds(10)
    };
    var dataB = new FunctionProfileData(new FunctionDebugInfo("B", 0x2000, 0x100)) {
      ExclusiveWeight = TimeSpan.Zero, Weight = TimeSpan.FromMilliseconds(20)
    };

    var report = new ProfileReport(
      new Dictionary<ProfileFunctionId, FunctionProfileData> { [idA] = dataA, [idB] = dataB },
      new ProfileCallTree(),
      TimeSpan.FromMilliseconds(30));

    // Core-owned IRTextFunction registry.
    var summary = new IRTextSummary(Module);
    var funcA = new IRTextFunction("A"); summary.AddFunction(funcA);
    var funcB = new IRTextFunction("B"); summary.AddFunction(funcB);
    var irById = new Dictionary<ProfileFunctionId, IRTextFunction> { [idA] = funcA, [idB] = funcB };

    var target = new ProfileData();
    ProfileReportMapper.ApplyReport(target, report,
      resolveFunction: id => irById.GetValueOrDefault(id),
      moduleIdByName: _ => ModuleId);

    // Function profiles carried through unchanged.
    Assert.AreEqual(2, target.FunctionProfiles.Count);
    Assert.AreSame(dataA, target.FunctionProfiles[idA]);
    Assert.AreSame(dataB, target.FunctionProfiles[idB]);

    // IRTextFunction resolver populated for navigation.
    Assert.AreSame(funcA, target.FunctionResolver[idA]);
    Assert.AreSame(funcB, target.FunctionResolver[idB]);
    Assert.AreSame(funcA, target.ResolveFunction(idA));

    // Module weight = sum of exclusive (leaf) weights per module.
    Assert.AreEqual(TimeSpan.FromMilliseconds(10), target.ModuleWeights[ModuleId]);

    // Call tree + totals.
    Assert.AreSame(report.CallTree, target.CallTree);
    Assert.AreEqual(TimeSpan.FromMilliseconds(30), target.TotalWeight);
    Assert.AreEqual(TimeSpan.FromMilliseconds(30), target.ProfileWeight);
  }

  [TestMethod]
  public void ApplyReport_UnresolvedFunction_SkipsResolverButKeepsProfileAndWeight() {
    var id = new ProfileFunctionId(Module, "Jit_0x1234");
    var data = new FunctionProfileData(new FunctionDebugInfo("Jit_0x1234", 0x0, 0x10)) {
      ExclusiveWeight = TimeSpan.FromMilliseconds(5), Weight = TimeSpan.FromMilliseconds(5)
    };

    var report = new ProfileReport(
      new Dictionary<ProfileFunctionId, FunctionProfileData> { [id] = data },
      new ProfileCallTree(),
      TimeSpan.FromMilliseconds(5));

    var target = new ProfileData();
    ProfileReportMapper.ApplyReport(target, report,
      resolveFunction: _ => null,          // no IR representation
      moduleIdByName: _ => ModuleId);

    Assert.IsTrue(target.FunctionProfiles.ContainsKey(id), "profile retained even without IR function");
    Assert.IsFalse(target.FunctionResolver.ContainsKey(id), "no resolver entry when IR function is null");
    Assert.AreEqual(TimeSpan.FromMilliseconds(5), target.ModuleWeights[ModuleId], "weight still counted");
  }
}
