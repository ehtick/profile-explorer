// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Profiling.Profiling;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Profiling.Symbols;
using ProfileExplorer.Profiling.Tests.Helpers;

namespace ProfileExplorer.Profiling.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public class CallTreeBuilderTests {
  private (IpResolver resolver, CallTreeBuilder builder) CreateBuilderWithFunctions(
    params (string module, long moduleBase, int moduleSize, string funcName, long funcRva, uint funcSize)[] functions) {
    var resolver = new IpResolver();
    foreach (var (module, moduleBase, moduleSize, _, _, _) in functions) {
      resolver.AddImage(module, moduleBase, moduleSize);
    }

    var grouped = functions.GroupBy(f => f.module);
    foreach (var group in grouped) {
      var funcList = group.Select(f => new FunctionDebugInfo(f.funcName, f.funcRva, f.funcSize)).ToList();
      resolver.SetFunctions(group.Key, funcList);
    }

    return (resolver, new CallTreeBuilder(resolver));
  }

  [TestMethod]
  public void SingleStack_CreatesLinearTree() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Root", 0x100, 0x50),
      ("test.dll", 0x1000, 0x10000, "Mid", 0x200, 0x50),
      ("test.dll", 0x1000, 0x10000, "Leaf", 0x300, 0x50));

    // Stack: leaf=0x1300, frames=[0x1300, 0x1200, 0x1100] (leaf-first).
    var sample = new SyntheticSample(0x1300, TimeSpan.FromMilliseconds(1), 1, 1, "test.dll", 0x1000,
      [0x1300, 0x1200, 0x1100]);
    builder.AddSamples([sample]);

    var roots = builder.Build().RootNodes;

    Assert.AreEqual(1, roots.Count); // One root function.
    Assert.AreEqual("Root", roots[0].FunctionName);
    Assert.AreEqual(1, roots[0].Children.Count);
    Assert.AreEqual("Mid", roots[0].Children[0].FunctionName);
    Assert.AreEqual(1, roots[0].Children[0].Children.Count);
    Assert.AreEqual("Leaf", roots[0].Children[0].Children[0].FunctionName);
  }

  [TestMethod]
  public void SharedPrefix_MergesNodes() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Root", 0x100, 0x50),
      ("test.dll", 0x1000, 0x10000, "LeafA", 0x200, 0x50),
      ("test.dll", 0x1000, 0x10000, "LeafB", 0x300, 0x50));

    builder.AddSamples([
      new SyntheticSample(0x1200, TimeSpan.FromMilliseconds(1), 1, 1, "test.dll", 0x1000, [0x1200, 0x1100]),
      new SyntheticSample(0x1300, TimeSpan.FromMilliseconds(1), 1, 1, "test.dll", 0x1000, [0x1300, 0x1100])
    ]);

    var roots = builder.Build().RootNodes;

    Assert.AreEqual(1, roots.Count); // Single root.
    Assert.AreEqual("Root", roots[0].FunctionName);
    Assert.AreEqual(2, roots[0].Children.Count); // Two children.
  }

  [TestMethod]
  public void InclusiveWeight_PropagatesToRoot() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Root", 0x100, 0x50),
      ("test.dll", 0x1000, 0x10000, "Leaf", 0x200, 0x50));

    builder.AddSamples([
      new SyntheticSample(0x1200, TimeSpan.FromMilliseconds(3), 1, 1, "test.dll", 0x1000, [0x1200, 0x1100])
    ]);

    var rootFunc = builder.Build().RootNodes[0];

    Assert.AreEqual(3.0, rootFunc.Weight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void ExclusiveWeight_OnlyOnLeaf() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Root", 0x100, 0x50),
      ("test.dll", 0x1000, 0x10000, "Leaf", 0x200, 0x50));

    builder.AddSamples([
      new SyntheticSample(0x1200, TimeSpan.FromMilliseconds(5), 1, 1, "test.dll", 0x1000, [0x1200, 0x1100])
    ]);

    var rootFunc = builder.Build().RootNodes[0];
    var leafFunc = rootFunc.Children[0];

    Assert.AreEqual(0.0, rootFunc.ExclusiveWeight.TotalMilliseconds, 0.01);
    Assert.AreEqual(5.0, leafFunc.ExclusiveWeight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void PerThreadWeights_Tracked() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Func", 0x100, 0x50));

    builder.AddSamples([
      new SyntheticSample(0x1100, TimeSpan.FromMilliseconds(3), 1, 10, "test.dll", 0x1000, [0x1100]),
      new SyntheticSample(0x1100, TimeSpan.FromMilliseconds(7), 1, 20, "test.dll", 0x1000, [0x1100])
    ]);

    var func = builder.Build().RootNodes[0];

    Assert.AreEqual(2, func.ThreadWeights.Count);
    Assert.AreEqual(3.0, func.ThreadWeights[10].Weight.TotalMilliseconds, 0.01);
    Assert.AreEqual(7.0, func.ThreadWeights[20].Weight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void EmptyStacks_Skipped() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Func", 0x100, 0x50));

    // Sample with no stack frames.
    builder.AddSamples([
      new SyntheticSample(0x1100, TimeSpan.FromMilliseconds(1), 1, 1, "test.dll", 0x1000)
    ]);

    Assert.AreEqual(0, builder.Build().RootNodes.Count);
  }

  [TestMethod]
  public void MultipleSamples_SameStack_AccumulatesWeight() {
    var (resolver, builder) = CreateBuilderWithFunctions(
      ("test.dll", 0x1000, 0x10000, "Func", 0x100, 0x50));

    var samples = Enumerable.Range(0, 10).Select(_ =>
      new SyntheticSample(0x1100, TimeSpan.FromMilliseconds(1), 1, 1, "test.dll", 0x1000, [0x1100])
    ).ToList();

    builder.AddSamples(samples);
    var roots = builder.Build().RootNodes;

    Assert.AreEqual(10.0, roots[0].Weight.TotalMilliseconds, 0.01);
    Assert.AreEqual(10.0, roots[0].ExclusiveWeight.TotalMilliseconds, 0.01);
  }
}
