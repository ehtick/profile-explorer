// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace ProfileExplorer.Core.Binary;

/// <summary>
/// Managed (.NET) debug-info reader core: holds the JIT-compiled method registry, IL-to-native
/// offset maps, and method code, and answers RVA/name lookups. Source-line resolution from a
/// managed (portable) PDB is delegated to <see cref="EnsureHasSourceLines"/>, which Profile
/// Explorer overrides with its TraceEvent-based loader (kept out of this TraceEvent-free library).
/// </summary>
public class ManagedDebugInfoProvider : ISymbolDebugInfo {
  protected readonly Dictionary<string, FunctionDebugInfo> functionMap_;
  protected readonly List<FunctionDebugInfo> functions_;
  protected readonly Dictionary<FunctionDebugInfo, List<(int ILOffset, int NativeOffset)>> methodILNativeMap_;
  protected Dictionary<long, MethodCode> methodCodeMap_;
  protected Machine architecture_;

  public ManagedDebugInfoProvider(Machine architecture) {
    architecture_ = architecture;
    functionMap_ = new Dictionary<string, FunctionDebugInfo>();
    functions_ = new List<FunctionDebugInfo>();
    methodILNativeMap_ = new Dictionary<FunctionDebugInfo, List<(int ILOffset, int NativeOffset)>>();
  }

  public Machine? Architecture => architecture_;

  public FunctionDebugInfo FindFunction(string functionName) {
    return functionMap_.TryGetValue(functionName, out var func) ? func : FunctionDebugInfo.Unknown;
  }

  public IEnumerable<FunctionDebugInfo> EnumerateFunctions() {
    return functions_;
  }

  public List<FunctionDebugInfo> GetSortedFunctions() {
    return functions_;
  }

  public FunctionDebugInfo FindFunctionByRVA(long rva) {
    return FunctionDebugInfo.BinarySearch(functions_, rva);
  }

  public SourceFileDebugInfo FindFunctionSourceFilePath(string functionName) {
    if (functionMap_.TryGetValue(functionName, out var funcInfo)) {
      return GetSourceFileInfo(funcInfo);
    }

    return SourceFileDebugInfo.Unknown;
  }

  public SourceFileDebugInfo FindSourceFilePathByRVA(long rva) {
    var funcInfo = FindFunctionByRVA(rva);

    if (EnsureHasSourceLines(funcInfo)) {
      return GetSourceFileInfo(funcInfo);
    }

    return SourceFileDebugInfo.Unknown;
  }

  public SourceLineDebugInfo FindSourceLineByRVA(long rva, bool includeInlinees) {
    var funcInfo = FindFunctionByRVA(rva);

    if (EnsureHasSourceLines(funcInfo)) {
      long offset = rva - funcInfo.StartRVA;
      return funcInfo.FindNearestLine(offset);
    }

    return SourceLineDebugInfo.Unknown;
  }

  public void Unload() {
  }

  public void Dispose() {
  }

  public bool PopulateSourceLines(FunctionDebugInfo funcInfo) {
    return true;
  }

  public void UpdateArchitecture(Machine architecture) {
    if (architecture_ == Machine.Unknown) {
      architecture_ = architecture;
    }
  }

  public MethodCode FindMethodCode(FunctionDebugInfo funcInfo) {
    if (methodCodeMap_ != null && methodCodeMap_.TryGetValue(funcInfo.RVA, out var code)) {
      return code;
    }

    return null;
  }

  public void AddFunctionInfo(FunctionDebugInfo funcInfo) {
    functions_.Add(funcInfo);
    functionMap_[funcInfo.Name] = funcInfo;
  }

  public void AddMethodILToNativeMap(FunctionDebugInfo functionDebugInfo,
                                     List<(int ILOffset, int NativeOffset)> ilOffsets) {
    methodILNativeMap_[functionDebugInfo] = ilOffsets;
  }

  public void LoadingCompleted() {
    functions_.Sort();
  }

  public void AddMethodCode(long codeAddress, MethodCode code) {
    methodCodeMap_ ??= new Dictionary<long, MethodCode>();
    methodCodeMap_[codeAddress] = code;
  }

  /// <summary>
  /// Loads source-line mappings for the given function from a managed (portable) PDB.
  /// The base library implementation has no PDB reader and returns false; Profile Explorer
  /// overrides this with a TraceEvent-based loader.
  /// </summary>
  protected virtual bool EnsureHasSourceLines(FunctionDebugInfo functionDebugInfo) {
    return functionDebugInfo is { IsUnknown: false, HasSourceLines: true };
  }

  protected static SourceFileDebugInfo GetSourceFileInfo(FunctionDebugInfo info) {
    return new SourceFileDebugInfo(info.SourceFileName,
                                   info.OriginalSourceFileName,
                                   info.FirstSourceLine.Line);
  }
}
