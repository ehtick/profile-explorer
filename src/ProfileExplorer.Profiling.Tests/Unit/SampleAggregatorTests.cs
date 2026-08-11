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
public class SampleAggregatorTests {
  private IpResolver CreateResolverWithFunction(string module, long moduleBase, int moduleSize,
                                                 string funcName, long funcRva, uint funcSize) {
    var resolver = new IpResolver();
    resolver.AddImage(module, moduleBase, moduleSize);
    var functions = new List<FunctionDebugInfo> {
      new(funcName, funcRva, funcSize)
    };
    resolver.SetFunctions(moduleBase, functions);
    return resolver;
  }

  [TestMethod]
  public void SingleSample_CreatesOneFunctionProfile() {
    var resolver = CreateResolverWithFunction("test.dll", 0x1000, 0x10000, "Foo", 0x100, 0x50);
    var aggregator = new SampleAggregator(resolver);
    var samples = SyntheticSampleBuilder.CreateUniform(1, "test.dll", 0x1100, TimeSpan.FromMilliseconds(1));

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(1, profiles.Count);
    var foo = profiles.First();
    Assert.AreEqual("Foo", foo.Key.FunctionName);
    Assert.AreEqual(1.0, foo.Value.ExclusiveWeight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void MultipleSamples_SameFunction_AggregatesWeight() {
    var resolver = CreateResolverWithFunction("test.dll", 0x1000, 0x10000, "Foo", 0x100, 0x50);
    var aggregator = new SampleAggregator(resolver);
    var samples = SyntheticSampleBuilder.CreateUniform(100, "test.dll", 0x1100, TimeSpan.FromMilliseconds(1));

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(1, profiles.Count);
    Assert.AreEqual(100.0, profiles.First().Value.ExclusiveWeight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void MultipleSamples_DifferentFunctions_SeparateProfiles() {
    var resolver = new IpResolver();
    resolver.AddImage("test.dll", 0x1000, 0x10000);
    resolver.SetFunctions(0x1000, [
      new FunctionDebugInfo("Foo", 0x100, 0x50),
      new FunctionDebugInfo("Bar", 0x200, 0x50),
      new FunctionDebugInfo("Baz", 0x300, 0x50)
    ]);

    var aggregator = new SampleAggregator(resolver);
    var weight = TimeSpan.FromMilliseconds(1);
    aggregator.AddSamples([
      new SyntheticSample(0x1100, weight, 1, 1, "test.dll", 0x1000),
      new SyntheticSample(0x1200, weight, 1, 1, "test.dll", 0x1000),
      new SyntheticSample(0x1300, weight, 1, 1, "test.dll", 0x1000)
    ]);

    var profiles = aggregator.Build();
    Assert.AreEqual(3, profiles.Count);
  }

  [TestMethod]
  public void SameFileName_DifferentBases_ResolveIndependently() {
    // Two different binaries both named "foo.dll" at different bases, with DIFFERENT function layouts.
    // Each base's samples must resolve to that binary's OWN function (symbols are keyed by base), not
    // both resolve through whichever function list registered last for the shared name.
    var resolver = new IpResolver();
    resolver.AddImage("foo.dll", 0x10000, 0x1000);
    resolver.AddImage("foo.dll", 0x20000, 0x1000);
    resolver.SetFunctions(0x10000, [new FunctionDebugInfo("Alpha", 0x100, 0x50)]);
    resolver.SetFunctions(0x20000, [new FunctionDebugInfo("Beta", 0x100, 0x50)]);

    var aggregator = new SampleAggregator(resolver);
    var w = TimeSpan.FromMilliseconds(1);
    aggregator.AddSamples([
      new SyntheticSample(0x10110, w, 1, 1, "foo.dll", 0x10000), // base 0x10000 -> Alpha
      new SyntheticSample(0x20110, w, 1, 1, "foo.dll", 0x20000)  // base 0x20000 -> Beta
    ]);

    var names = aggregator.Build().Select(p => p.Key.FunctionName).OrderBy(n => n).ToArray();
    CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, names,
      "Each same-named binary's samples must resolve to its own function, not merge.");
  }

  [TestMethod]
  public void InstructionWeights_AggregatesPerOffset() {
    var resolver = CreateResolverWithFunction("test.dll", 0x1000, 0x10000, "Foo", 0x100, 0x50);
    var aggregator = new SampleAggregator(resolver);
    var weight = TimeSpan.FromMilliseconds(1);

    // 10 samples each at 5 different offsets within the function.
    var samples = new List<IProfileSample>();
    for (int offset = 0; offset < 5; offset++) {
      for (int i = 0; i < 10; i++) {
        samples.Add(new SyntheticSample(0x1100 + offset * 4, weight, 1, 1, "test.dll", 0x1000));
      }
    }

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(1, profiles.Count);
    Assert.AreEqual(5, profiles.First().Value.InstructionWeight.Count);
    Assert.AreEqual(50.0, profiles.First().Value.ExclusiveWeight.TotalMilliseconds, 0.01);
  }

  [TestMethod]
  public void EmptySamples_ReturnsEmptyProfiles() {
    var resolver = new IpResolver();
    var aggregator = new SampleAggregator(resolver);

    aggregator.AddSamples([]);
    var profiles = aggregator.Build();

    Assert.AreEqual(0, profiles.Count);
  }

  [TestMethod]
  public void UserImagelessSample_BucketedToUnknown() {
    var resolver = new IpResolver();
    var aggregator = new SampleAggregator(resolver);
    // User-space leaf IP (below the kernel threshold) with no image: Core credits these to a synthetic
    // "[Unknown Module]" so the CPU time is counted; the library mirrors that with a single bucket.
    var samples = new List<IProfileSample> {
      new SyntheticSample(0x1000, TimeSpan.FromMilliseconds(1), 1, 1, null, 0)
    };

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(1, profiles.Count, "user-space imageless sample should be bucketed, not dropped");
    Assert.AreEqual("(unknown)", profiles.First().Key.FunctionName);
    Assert.AreEqual(1.0, profiles.First().Value.ExclusiveWeight.TotalMilliseconds, 0.01);
    Assert.AreEqual(1.0, aggregator.TotalWeight.TotalMilliseconds, 0.01,
                    "imageless user CPU must count toward the total denominator");
  }

  [TestMethod]
  public void KernelImagelessSample_Dropped() {
    var resolver = new IpResolver();
    var aggregator = new SampleAggregator(resolver);
    // Kernel-space leaf IP with no image: Core adds a null frame (credited to no function), so the
    // library drops it — it must NOT count toward the total.
    var samples = new List<IProfileSample> {
      new SyntheticSample(unchecked((long)0xFFFF800000001000), TimeSpan.FromMilliseconds(1), 1, 1, null, 0)
    };

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(0, profiles.Count, "kernel-space imageless sample should be dropped");
    Assert.AreEqual(0.0, aggregator.TotalWeight.TotalMilliseconds, 0.01, "must not count toward the total");
  }

  [TestMethod]
  public void PointerSize_DerivedFromRegisteredImages() {
    var r64 = new IpResolver();
    r64.AddImage("ntdll.dll", 0x7FF800000000, 0x100000); // above 4 GB => 64-bit trace
    Assert.AreEqual(8, r64.PointerSize);

    var r32 = new IpResolver();
    r32.AddImage("app32.exe", 0x00400000, 0x10000); // everything below 4 GB => 32-bit trace
    Assert.AreEqual(4, r32.PointerSize);

    Assert.AreEqual(8, new IpResolver().PointerSize, "defaults to 64-bit when no images are registered");
  }

  [TestMethod]
  public void PointerSize_ExplicitSet_WinsOverImageDerivation() {
    // The host (FUN AI) sets the authoritative trace pointer size; it must win over the image-base
    // derivation and survive later AddImage calls.
    var resolver = new IpResolver { PointerSize = 4 };
    Assert.AreEqual(4, resolver.PointerSize);

    resolver.AddImage("ntdll.dll", 0x7FF800000000, 0x100000); // above 4 GB would derive 8...
    Assert.AreEqual(4, resolver.PointerSize, "explicit value wins and survives AddImage");

    // Clearing it (0) falls back to derivation from the registered images (now 64-bit).
    resolver.PointerSize = 0;
    Assert.AreEqual(8, resolver.PointerSize, "0 reverts to image-base derivation");
  }

  [TestMethod]
  public void KernelImageless_32BitTrace_DroppedNotBucketed() {
    // Only a below-4 GB image is registered, so the resolver derives a 32-bit pointer size (4) where
    // the kernel split is 0x80000000 (not the 64-bit 0xFFFF... threshold). A 32-bit kernel imageless
    // leaf must be dropped, NOT bucketed into "(unknown)" and counted (the reviewed hard-coded-8 bug).
    var resolver = new IpResolver();
    resolver.AddImage("app32.exe", 0x00400000, 0x10000);
    Assert.AreEqual(4, resolver.PointerSize);

    var aggregator = new SampleAggregator(resolver);
    var samples = new List<IProfileSample> {
      new SyntheticSample(0x81234567, TimeSpan.FromMilliseconds(1), 1, 1, null, 0) // 32-bit kernel addr
    };

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.AreEqual(0, profiles.Count, "32-bit kernel imageless leaf should be dropped");
    Assert.AreEqual(0.0, aggregator.TotalWeight.TotalMilliseconds, 0.01, "must not count toward the total");
  }

  [TestMethod]
  public void UserImagelessLeaf_ResolvedCallersStillCreditedInclusive() {
    // Reviewer regression: a user-space imageless leaf must NOT stop stack processing. Core resolves
    // the leaf into a synthetic frame and keeps walking, so resolved caller frames below it still
    // receive inclusive weight (the leaf's `continue` used to drop the whole caller walk).
    var resolver = new IpResolver();
    resolver.AddImage("test.dll", 0x1000, 0x10000);
    resolver.SetFunctions(0x1000, [new FunctionDebugInfo("Bar", 0x200, 0x50)]);

    var aggregator = new SampleAggregator(resolver);
    var weight = TimeSpan.FromMilliseconds(1);

    // Leaf is user-space imageless (ImageName null); its caller (index 1) resolves to test.dll!Bar.
    var samples = new List<IProfileSample> {
      new SyntheticSample(0x800000, weight, 1, 1, null, 0, new long[] { 0x800000, 0x1200 })
    };

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    var unknown = profiles.First(p => p.Key.FunctionName == "(unknown)").Value;
    Assert.AreEqual(weight, unknown.ExclusiveWeight, "imageless leaf keeps its self weight in (unknown)");

    var bar = profiles.First(p => p.Key.FunctionName == "Bar").Value;
    Assert.AreEqual(weight, bar.Weight, "resolved caller above an imageless leaf must get inclusive weight");
    Assert.AreEqual(TimeSpan.Zero, bar.ExclusiveWeight, "caller is not the leaf, so no exclusive weight");
    Assert.AreEqual(weight, aggregator.TotalWeight, "user-imageless leaf counts toward the total");
  }

  [TestMethod]
  public void KernelImagelessLeaf_ResolvedCallersStillCreditedInclusive() {
    // Same latent bug on the kernel path: a kernel-space imageless leaf is a null frame (no self
    // weight, excluded from the total), but Core still walks the stack, so resolved callers below it
    // get inclusive weight. Only the leaf is dropped — not the whole stack.
    var resolver = new IpResolver();
    resolver.AddImage("test.dll", 0x1000, 0x10000);
    resolver.SetFunctions(0x1000, [new FunctionDebugInfo("Bar", 0x200, 0x50)]);

    var aggregator = new SampleAggregator(resolver);
    var weight = TimeSpan.FromMilliseconds(1);

    long kernelLeaf = unchecked((long)0xFFFF800000001000);
    var samples = new List<IProfileSample> {
      new SyntheticSample(kernelLeaf, weight, 1, 1, null, 0, new long[] { kernelLeaf, 0x1200 })
    };

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();

    Assert.IsFalse(profiles.Any(p => p.Key.FunctionName == "(unknown)"),
                   "kernel-space imageless leaf must not be bucketed");
    var bar = profiles.First(p => p.Key.FunctionName == "Bar").Value;
    Assert.AreEqual(weight, bar.Weight, "resolved caller above a kernel imageless leaf must get inclusive weight");
    Assert.AreEqual(TimeSpan.Zero, aggregator.TotalWeight, "kernel imageless leaf itself does not count toward the total");
  }

  [TestMethod]
  public void PercentCalculation_RelativeToTotalWeight() {
    var resolver = new IpResolver();
    resolver.AddImage("test.dll", 0x1000, 0x10000);
    resolver.SetFunctions(0x1000, [
      new FunctionDebugInfo("Foo", 0x100, 0x50),
      new FunctionDebugInfo("Bar", 0x200, 0x50)
    ]);

    var aggregator = new SampleAggregator(resolver);
    var weight = TimeSpan.FromMilliseconds(1);

    // 75 samples to Foo, 25 to Bar.
    var samples = new List<IProfileSample>();
    for (int i = 0; i < 75; i++)
      samples.Add(new SyntheticSample(0x1100, weight, 1, 1, "test.dll", 0x1000));
    for (int i = 0; i < 25; i++)
      samples.Add(new SyntheticSample(0x1200, weight, 1, 1, "test.dll", 0x1000));

    aggregator.AddSamples(samples);
    var profiles = aggregator.Build();
    double total = aggregator.TotalWeight.TotalMilliseconds;

    var foo = profiles.First(p => p.Key.FunctionName == "Foo");
    var bar = profiles.First(p => p.Key.FunctionName == "Bar");

    Assert.AreEqual(75.0, foo.Value.ExclusiveWeight.TotalMilliseconds / total * 100, 0.1);
    Assert.AreEqual(25.0, bar.Value.ExclusiveWeight.TotalMilliseconds / total * 100, 0.1);
  }
}
