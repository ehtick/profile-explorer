// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Profiling.Symbols;

namespace ProfileExplorer.Profiling.Tests.Helpers;

/// <summary>
/// Locates test data (ETL, PDB, DLL) relative to the test assembly output directory.
/// Shares test data with ProfileExplorerCoreTests/TestData/.
/// </summary>
public static class TestDataHelper {
  private static string? testDataRoot_;

  /// <summary>
  /// Get the root TestData directory by walking up from the assembly output.
  /// Looks for ProfileExplorerCoreTests/TestData/ relative to the src/ directory.
  /// </summary>
  public static string GetTestDataRoot() {
    if (testDataRoot_ != null) return testDataRoot_;

    // Walk up from bin/Release/net8.0-windows/ to find the src directory.
    string assemblyDir = Path.GetDirectoryName(typeof(TestDataHelper).Assembly.Location)!;
    var dir = new DirectoryInfo(assemblyDir);

    while (dir != null) {
      // Check if this is the src/ directory containing ProfileExplorerCoreTests.
      string candidatePath = Path.Combine(dir.FullName, "ProfileExplorerCoreTests", "TestData");
      if (Directory.Exists(candidatePath)) {
        testDataRoot_ = candidatePath;
        return testDataRoot_;
      }

      dir = dir.Parent;
    }

    throw new DirectoryNotFoundException(
      $"Could not find ProfileExplorerCoreTests/TestData/ directory from {assemblyDir}");
  }

  public static string GetTracePath(string testCaseName, string fileName = "trace.etl") =>
    Path.Combine(GetTestDataRoot(), "Traces", testCaseName, fileName);

  public static string GetSymbolsPath(string testCaseName) =>
    Path.Combine(GetTestDataRoot(), "Symbols", testCaseName);

  public static string GetSymbolFilePath(string testCaseName, string fileName) =>
    Path.Combine(GetTestDataRoot(), "Symbols", testCaseName, fileName);

  public static string GetBinariesPath(string testCaseName) =>
    Path.Combine(GetTestDataRoot(), "Binaries", testCaseName);

  public static string GetBinaryFilePath(string testCaseName, string fileName) =>
    Path.Combine(GetTestDataRoot(), "Binaries", testCaseName, fileName);

  public static bool HasTestData(string testCaseName) {
    try {
      return File.Exists(GetTracePath(testCaseName));
    }
    catch {
      return false;
    }
  }

  /// <summary>
  /// Returns functions whose RVA appears exactly once in the symbol list (i.e. not affected by
  /// ICF/COMDAT folding, where several names share the same RVA). Folded functions have no stable
  /// RVA-&gt;name mapping, so tests that need a deterministic round-trip should select from these.
  /// </summary>
  public static List<FunctionDebugInfo> GetUniqueRvaFunctions(PdbSymbolProvider provider) {
    var functions = provider.GetSortedFunctions();
    var rvaCounts = new Dictionary<long, int>(functions.Count);

    foreach (var func in functions) {
      rvaCounts.TryGetValue(func.RVA, out int count);
      rvaCounts[func.RVA] = count + 1;
    }

    return functions.Where(f => f.Size > 0 && rvaCounts[f.RVA] == 1).ToList();
  }

  // MsoTrace constants.
  public const string MsoTrace = "MsoTrace";
  public const string MsoPdbFile = "Mso20Win32Client.pdb";
  public const string MsoDllFile = "Mso20win32client.dll";
  public const string MsoModuleName = "Mso20win32client.dll";
  public const string MsoTopFunction = "Mso::Experiment::EcsNS::Private::SortByParameterGroups";
}
