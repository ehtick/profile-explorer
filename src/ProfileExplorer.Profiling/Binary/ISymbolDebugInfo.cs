// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace ProfileExplorer.Core.Binary;

/// <summary>
/// Decoupled debug-info reader surface: RVA/name-based symbol and source-line lookups
/// with no dependency on Profile Explorer's IR/document model. This is the interface the
/// library's binary/disassembly code depends on. Profile Explorer's IR-aware
/// <see cref="IDebugInfoProvider"/> derives from this and adds IR-coupled members.
/// </summary>
public interface ISymbolDebugInfo : IDisposable {
  Machine? Architecture { get; }
  void Unload();
  IEnumerable<FunctionDebugInfo> EnumerateFunctions();
  List<FunctionDebugInfo> GetSortedFunctions();
  FunctionDebugInfo FindFunction(string functionName);
  FunctionDebugInfo FindFunctionByRVA(long rva);
  bool PopulateSourceLines(FunctionDebugInfo funcInfo);
  SourceFileDebugInfo FindFunctionSourceFilePath(string functionName);
  SourceFileDebugInfo FindSourceFilePathByRVA(long rva);
  SourceLineDebugInfo FindSourceLineByRVA(long rva, bool includeInlinees = false);
}
