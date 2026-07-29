// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Profiling.Profiling;
using ProfileExplorer.Profiling.Symbols;
using ProfileExplorer.Profiling.Tests.Helpers;

namespace ProfileExplorer.Profiling.Tests.Integration;

/// <summary>
/// End-to-end checks that <see cref="IpResolver"/>'s provider path composes correctly against a real
/// DIA-backed <see cref="PdbSymbolProvider"/> (the MSO PDB), complementing the deterministic stub
/// coverage in <c>IpResolverProviderTests</c>. The split-chunk case is discovered at runtime rather
/// than hard-coded (PGO layout drifts across PDB builds); it reports <c>Inconclusive</c> if the PDB
/// exposes no list-missing-but-DIA-resolvable address, so it never fails spuriously.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class IpResolverRealProviderTests {
  private const long ModuleBase = 0x140000000;
  private static string PdbPath => TestDataHelper.GetSymbolFilePath(TestDataHelper.MsoTrace, TestDataHelper.MsoPdbFile);

  [ClassInitialize]
  public static void ClassInit(TestContext _) {
    // Point at msdia140.dll for side-loading (located in src/external/), mirroring PdbSymbolProviderTests.
    string assemblyDir = Path.GetDirectoryName(typeof(IpResolverRealProviderTests).Assembly.Location)!;
    var dir = new DirectoryInfo(assemblyDir);
    while (dir != null) {
      string candidate = Path.Combine(dir.FullName, "external", "msdia140.dll");
      if (File.Exists(candidate)) { PdbSymbolProvider.MsDiaPath = candidate; break; }
      candidate = Path.Combine(dir.FullName, "src", "external", "msdia140.dll");
      if (File.Exists(candidate)) { PdbSymbolProvider.MsDiaPath = candidate; break; }
      dir = dir.Parent;
    }
  }

  private static PdbSymbolProvider? TryLoadProvider() {
    if (!File.Exists(PdbPath)) return null;
    var provider = new PdbSymbolProvider();
    if (!provider.LoadDebugInfo(PdbPath)) { provider.Dispose(); return null; }
    return provider;
  }

  private static IpResolver BuildResolver(PdbSymbolProvider provider, List<FunctionDebugInfo> functions,
                                          bool withProvider) {
    long maxEndRva = functions.Count > 0 ? functions.Max(f => f.EndRVA) : 0;
    int size = (int)Math.Min(maxEndRva + 0x10000, int.MaxValue);
    var resolver = new IpResolver();
    resolver.AddImage(TestDataHelper.MsoModuleName, ModuleBase, size);
    resolver.SetFunctions(TestDataHelper.MsoModuleName, functions, withProvider ? provider : null);
    return resolver;
  }

  [TestMethod]
  public void IpResolver_WithRealProvider_ResolvesListedFunctionsIdenticallyToProvider() {
    using var provider = TryLoadProvider();
    if (provider == null) { Assert.Inconclusive("MSO PDB or DIA SDK not available."); return; }

    var functions = provider.GetSortedFunctions();
    var resolver = BuildResolver(provider, functions, withProvider: true);

    // Wiring smoke test against real DIA: AddImage + SetFunctions(provider) + Resolve must route an IP
    // to whatever the provider resolves for that module-relative RVA. Use unique-RVA functions so the
    // provider's own answer is deterministic, and resolve at the start RVA.
    var uniqueRva = TestDataHelper.GetUniqueRvaFunctions(provider);
    if (uniqueRva.Count == 0) { Assert.Inconclusive("No unique-RVA functions in PDB."); return; }

    int verified = 0;
    foreach (var func in uniqueRva.Take(100)) {
      var expected = provider.FindFunctionByRVA(func.RVA);
      var resolved = resolver.Resolve(ModuleBase + func.RVA);

      Assert.IsNotNull(resolved);
      Assert.AreEqual(expected?.Name, resolved.FunctionName,
        $"IpResolver must return the provider's function for RVA 0x{func.RVA:X}.");
      verified++;
    }

    Assert.IsTrue(verified > 0, "Should have resolved at least one function through the real provider.");
  }

  [TestMethod]
  public void IpResolver_WithRealProvider_CreditsListMissingAddressToProvider() {
    using var provider = TryLoadProvider();
    if (provider == null) { Assert.Inconclusive("MSO PDB or DIA SDK not available."); return; }

    var functions = provider.GetSortedFunctions();
    if (functions.Count < 2) { Assert.Inconclusive("Not enough functions to probe gaps."); return; }

    // Discover an address the contiguous list does NOT cover (plain BinarySearch misses) but DIA still
    // resolves to a named function — a PGO-split cold chunk or gap-covered code. Probe the gaps between
    // consecutive listed functions; cap probes so a PDB with no such address ends quickly.
    long foundRva = -1;
    string? expectedName = null;
    int probes = 0;

    for (int i = 0; i < functions.Count - 1 && probes < 2000 && foundRva < 0; i++) {
      long gapStart = functions[i].EndRVA + 1;
      long gapEnd = functions[i + 1].StartRVA; // exclusive
      if (gapStart >= gapEnd) continue; // no gap (adjacent or overlapping)

      long probeRva = gapStart;
      probes++;

      // Must be missed by the plain contiguous lookup (the pre-fix path)...
      if (FunctionDebugInfo.BinarySearch(functions, probeRva) != null) continue;

      // ...but resolvable to a named function via the provider's DIA fallback.
      var viaProvider = provider.FindFunctionByRVA(probeRva);
      if (viaProvider != null && !string.IsNullOrEmpty(viaProvider.Name)) {
        foundRva = probeRva;
        expectedName = viaProvider.Name;
      }
    }

    if (foundRva < 0) {
      Assert.Inconclusive("No list-missing-but-DIA-resolvable address found in this PDB build.");
      return;
    }

    // With the provider, the list-missing address is credited to the parent function.
    var withProvider = BuildResolver(provider, functions, withProvider: true);
    var resolved = withProvider.Resolve(ModuleBase + foundRva);
    Assert.IsNotNull(resolved);
    Assert.AreEqual(expectedName, resolved.FunctionName,
      $"List-missing address 0x{foundRva:X} must be credited via the provider.");

    // Contrast: without the provider, the same address falls through to module-only "unknown" — the
    // exact attribution loss the provider path fixes.
    var listOnly = BuildResolver(provider, functions, withProvider: false);
    var listOnlyResolved = listOnly.Resolve(ModuleBase + foundRva);
    Assert.IsNotNull(listOnlyResolved);
    Assert.IsNull(listOnlyResolved.FunctionName,
      $"Without the provider, list-missing address 0x{foundRva:X} is lost to module-only resolution.");
  }
}
