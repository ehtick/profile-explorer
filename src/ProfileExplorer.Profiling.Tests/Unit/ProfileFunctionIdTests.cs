// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core.Profile;

namespace ProfileExplorer.Profiling.Tests.Unit;

/// <summary>
/// Locks the identity invariant for <see cref="ProfileFunctionId"/>: a <c>default</c> struct (which
/// bypasses the constructor) must compare, hash, and key equal to an explicitly built empty id.
/// Without this, "unknown" frames split across null-keyed and empty-keyed aggregation buckets.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ProfileFunctionIdTests {
  [TestMethod]
  public void Default_NullNames_And_EmptyNames_AreEqual() {
    var fromDefault = default(ProfileFunctionId);
    var fromNulls = new ProfileFunctionId(null, null);
    var fromEmpty = new ProfileFunctionId(string.Empty, string.Empty);

    Assert.AreEqual(fromDefault, fromNulls);
    Assert.AreEqual(fromDefault, fromEmpty);
    Assert.AreEqual(fromNulls, fromEmpty);
    Assert.IsTrue(fromDefault == fromEmpty);
    Assert.IsFalse(fromDefault != fromNulls);
  }

  [TestMethod]
  public void Default_NullNames_And_EmptyNames_ShareHashCode() {
    Assert.AreEqual(default(ProfileFunctionId).GetHashCode(), new ProfileFunctionId(null, null).GetHashCode());
    Assert.AreEqual(default(ProfileFunctionId).GetHashCode(), new ProfileFunctionId(string.Empty, string.Empty).GetHashCode());
  }

  [TestMethod]
  public void UnknownFormsCollapseToSingleDictionaryKey() {
    var map = new Dictionary<ProfileFunctionId, int>();
    map[default] = 1;
    map[new ProfileFunctionId(null, null)] = 2;
    map[new ProfileFunctionId(string.Empty, string.Empty)] = 3;

    Assert.AreEqual(1, map.Count);
    Assert.AreEqual(3, map[default]);
  }

  [TestMethod]
  public void Names_AreNeverNull_EvenForDefault() {
    var id = default(ProfileFunctionId);
    Assert.AreEqual(string.Empty, id.ModuleName);
    Assert.AreEqual(string.Empty, id.FunctionName);
  }

  [TestMethod]
  public void IsUnknown_TrueForEmptyForms_FalseForNamedFunction() {
    Assert.IsTrue(default(ProfileFunctionId).IsUnknown);
    Assert.IsTrue(new ProfileFunctionId(null, null).IsUnknown);
    Assert.IsTrue(new ProfileFunctionId("mod.dll", string.Empty).IsUnknown);
    Assert.IsFalse(new ProfileFunctionId("mod.dll", "Func").IsUnknown);
  }

  [TestMethod]
  public void DistinctNamedIds_AreNotEqual_AndPreserveNames() {
    var a = new ProfileFunctionId("mod.dll", "Func");
    var b = new ProfileFunctionId("mod.dll", "Other");

    Assert.AreNotEqual(a, b);
    Assert.AreEqual("mod.dll", a.ModuleName);
    Assert.AreEqual("Func", a.FunctionName);
    Assert.AreNotEqual(default(ProfileFunctionId), a);
  }
}
