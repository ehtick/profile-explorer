// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Reflection.PortableExecutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Profiling.Profiling;

namespace ProfileExplorer.Profiling.Tests.Unit;

/// <summary>
/// Covers <see cref="IpResolver"/>'s provider-based lookup path (registered via
/// <c>SetFunctions(..., debugInfo:)</c>): the provider result takes precedence over the contiguous
/// sorted list, and the per-module cache memoizes both hits and misses so a provider is queried at
/// most once per RVA. Guards the threading/caching code that mirrors Core's FindFunctionByRVA.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class IpResolverProviderTests {
  private const long ModuleBase = 0x10000;
  private const int ModuleSize = 0x100000;
  private const long FuncRva = 0x2000;
  private const uint FuncSize = 0x100;
  // An absolute IP that falls inside [FuncRva, FuncRva + FuncSize) once the module base is subtracted.
  private const long SampleIp = ModuleBase + FuncRva + 0x50;

  /// <summary>
  /// Configurable <see cref="ISymbolDebugInfo"/> stub. Only <see cref="FindFunctionByRVA"/> is
  /// exercised; it delegates to a caller-supplied function and counts invocations so tests can assert
  /// the cache queries the provider at most once per RVA.
  /// </summary>
  private sealed class StubSymbolDebugInfo : ISymbolDebugInfo {
    private readonly Func<long, FunctionDebugInfo?> resolve_;

    public StubSymbolDebugInfo(Func<long, FunctionDebugInfo?> resolve) {
      resolve_ = resolve;
    }

    public int FindByRvaCallCount { get; private set; }

    public FunctionDebugInfo FindFunctionByRVA(long rva) {
      FindByRvaCallCount++;
      return resolve_(rva)!; // May legitimately be null (a miss); IpResolver handles that.
    }

    public Machine? Architecture => null;
    public void Unload() { }
    public IEnumerable<FunctionDebugInfo> EnumerateFunctions() => Array.Empty<FunctionDebugInfo>();
    public List<FunctionDebugInfo> GetSortedFunctions() => new();
    public FunctionDebugInfo FindFunction(string functionName) => FunctionDebugInfo.Unknown;
    public bool PopulateSourceLines(FunctionDebugInfo funcInfo) => false;
    public SourceFileDebugInfo FindFunctionSourceFilePath(string functionName) => SourceFileDebugInfo.Unknown;
    public SourceFileDebugInfo FindSourceFilePathByRVA(long rva) => SourceFileDebugInfo.Unknown;
    public SourceLineDebugInfo FindSourceLineByRVA(long rva, bool includeInlinees = false) => SourceLineDebugInfo.Unknown;
    public void Dispose() { }
  }

  private static IpResolver CreateResolver(ISymbolDebugInfo? provider,
                                           string listFuncName = "ListFunc") {
    var resolver = new IpResolver();
    resolver.AddImage("mod.dll", ModuleBase, ModuleSize);
    // The contiguous list would resolve SampleIp to listFuncName; a registered provider must win.
    var functions = new List<FunctionDebugInfo> { new(listFuncName, FuncRva, FuncSize) };
    resolver.SetFunctions(ModuleBase, functions, provider);
    return resolver;
  }

  [TestMethod]
  public void RegisteredProvider_ResultTakesPrecedenceOverContiguousList() {
    var providerFunc = new FunctionDebugInfo("ProviderFunc", FuncRva, FuncSize);
    var stub = new StubSymbolDebugInfo(rva => rva >= FuncRva && rva < FuncRva + FuncSize ? providerFunc : null);
    var resolver = CreateResolver(stub, listFuncName: "ListFunc");

    var resolved = resolver.Resolve(SampleIp);

    Assert.IsNotNull(resolved);
    Assert.AreEqual("ProviderFunc", resolved.FunctionName,
      "Provider-registered modules must resolve through the provider, not the contiguous list.");
  }

  [TestMethod]
  public void RegisteredProvider_Hit_IsCachedAcrossRepeatedCalls() {
    var providerFunc = new FunctionDebugInfo("ProviderFunc", FuncRva, FuncSize);
    var stub = new StubSymbolDebugInfo(_ => providerFunc);
    var resolver = CreateResolver(stub);

    var first = resolver.Resolve(SampleIp);
    var second = resolver.Resolve(SampleIp);

    Assert.AreEqual("ProviderFunc", first!.FunctionName);
    Assert.AreEqual("ProviderFunc", second!.FunctionName);
    Assert.AreEqual(1, stub.FindByRvaCallCount,
      "A repeated RVA must be served from the cache, not re-queried on the provider.");
  }

  [TestMethod]
  public void RegisteredProvider_Miss_DoesNotThrow_IsConsistent_AndNegativeCached() {
    // Provider always misses (returns null) even though the contiguous list covers the RVA — a
    // registered provider owns resolution, so a miss yields a module-only result, not the list entry.
    var stub = new StubSymbolDebugInfo(_ => null);
    var resolver = CreateResolver(stub);

    var first = resolver.Resolve(SampleIp);
    var second = resolver.Resolve(SampleIp);

    Assert.IsNotNull(first);
    Assert.IsNull(first!.FunctionName, "A provider miss must produce a module-only (unnamed) result.");
    Assert.IsNull(second!.FunctionName);
    Assert.AreEqual(first.FunctionName, second.FunctionName, "Repeated misses must be consistent.");
    Assert.AreEqual("mod.dll", first.ModuleName);
    Assert.AreEqual(FuncRva + 0x50, first.Rva, "A module-only result keeps the module-relative RVA.");
    Assert.AreEqual(1, stub.FindByRvaCallCount,
      "The negative result must be cached so a repeated miss doesn't re-query the provider.");
  }

  [TestMethod]
  public void NoProvider_FallsBackToContiguousList() {
    var resolver = CreateResolver(provider: null, listFuncName: "ListFunc");

    var resolved = resolver.Resolve(SampleIp);

    Assert.IsNotNull(resolved);
    Assert.AreEqual("ListFunc", resolved.FunctionName,
      "Without a registered provider, resolution must use the contiguous sorted list.");
  }

  /// <summary>
  /// Characterizes the PGO-split-chunk case that motivated routing through the provider: an address in
  /// a cold chunk that the contiguous list does NOT cover. The plain list resolves it to a module-only
  /// "unknown" (the pre-fix regression), whereas the provider — simulating DIA's FindFunctionByRVA,
  /// which maps the cold chunk back to its parent — credits it to the parent function. This is the
  /// behavior both engines now share, since Core also resolves through the same provider.
  /// </summary>
  [TestMethod]
  public void SplitChunkAddress_MissingFromContiguousList_ResolvesToParentViaProvider() {
    // Cold chunk lives far from the parent's primary [FuncRva, FuncRva + FuncSize) range, so no list
    // entry covers it — a plain BinarySearch returns null.
    const long ColdChunkRva = 0x9000;
    const long ColdChunkIp = ModuleBase + ColdChunkRva + 0x20;

    // DIA maps a cold-chunk RVA back to the parent symbol: same name, parent's primary RVA/size.
    var parentFunc = new FunctionDebugInfo("ParentFunc", FuncRva, FuncSize);
    var stub = new StubSymbolDebugInfo(rva =>
      rva >= ColdChunkRva && rva < ColdChunkRva + FuncSize ? parentFunc : null);

    // Parent's primary chunk is the only entry in the contiguous list; the cold chunk is absent.
    var withProvider = new IpResolver();
    withProvider.AddImage("mod.dll", ModuleBase, ModuleSize);
    withProvider.SetFunctions(ModuleBase, new List<FunctionDebugInfo> { parentFunc }, stub);

    var resolved = withProvider.Resolve(ColdChunkIp);
    Assert.IsNotNull(resolved);
    Assert.AreEqual("ParentFunc", resolved.FunctionName,
      "A split cold-chunk address must be credited to its parent via the provider.");
    Assert.AreEqual(FuncRva, resolved.Rva, "It resolves to the parent's primary chunk RVA.");

    // Contrast: without the provider, the same cold-chunk address is lost to module-only "unknown" —
    // the exact regression the provider path fixes.
    var listOnly = new IpResolver();
    listOnly.AddImage("mod.dll", ModuleBase, ModuleSize);
    listOnly.SetFunctions(ModuleBase, new List<FunctionDebugInfo> { parentFunc });

    var listOnlyResolved = listOnly.Resolve(ColdChunkIp);
    Assert.IsNotNull(listOnlyResolved);
    Assert.IsNull(listOnlyResolved.FunctionName,
      "Without the provider, the cold-chunk address is not covered by the list and is lost.");
  }
}
