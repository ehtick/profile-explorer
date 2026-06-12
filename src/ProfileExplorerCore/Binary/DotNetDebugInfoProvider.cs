// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using Microsoft.Diagnostics.Symbols;
using ProfileExplorer.Core.IR;
using ProfileExplorer.Core.IR.Tags;
using ProfileExplorer.Core.Providers;
using ProfileExplorer.Core.Settings;
using ProfileExplorer.Core.Utilities;

namespace ProfileExplorer.Core.Binary;

/// <summary>
/// Profile Explorer's managed (.NET) debug-info provider. Extends the TraceEvent-free
/// <see cref="ManagedDebugInfoProvider"/> reading core (in ProfileExplorer.Profiling) with IR
/// source-location annotation and TraceEvent-based managed (portable) PDB source-line loading.
/// </summary>
public class DotNetDebugInfoProvider : ManagedDebugInfoProvider, IDebugInfoProvider {
  private bool hasManagedSymbolFileFailure_;

  public DotNetDebugInfoProvider(Machine architecture) : base(architecture) {
  }

  public SymbolFileDescriptor ManagedSymbolFile { get; set; }
  public string ManagedAsmFilePath { get; set; }
  public SymbolFileSourceSettings SymbolSettings { get; set; }

  public bool AnnotateSourceLocations(FunctionIR function, IRTextFunction textFunc) {
    return AnnotateSourceLocations(function, textFunc.Name);
  }

  public bool AnnotateSourceLocations(FunctionIR function, FunctionDebugInfo funcInfo) {
    var metadataTag = function.GetTag<AssemblyMetadataTag>();

    if (metadataTag == null) {
      return false;
    }

    if (!EnsureHasSourceLines(funcInfo)) {
      return false;
    }

    foreach (var pair in metadataTag.OffsetToElementMap) {
      var lineInfo = funcInfo.FindNearestLine(pair.Key);

      if (!lineInfo.IsUnknown) {
        var locationTag = pair.Value.GetOrAddTag<SourceLocationTag>();
        locationTag.Reset(); // Tag may be already populated.
        locationTag.Line = lineInfo.Line;
        locationTag.Column = lineInfo.Column;
      }
    }

    return true;
  }

  public bool AnnotateSourceLocations(FunctionIR function, string functionName) {
    var funcInfo = FindFunction(functionName);

    if (funcInfo == null) {
      return false;
    }

    return AnnotateSourceLocations(function, funcInfo);
  }

  public SourceFileDebugInfo FindFunctionSourceFilePath(IRTextFunction textFunc) {
    return FindFunctionSourceFilePath(textFunc.Name);
  }

  public bool LoadDebugInfo(DebugFileSearchResult debugFile, IDebugInfoProvider other = null) {
    return true;
  }

  public bool LoadDebugInfo(string debugFilePath, IDebugInfoProvider other = null) {
    return true;
  }

  protected override bool EnsureHasSourceLines(FunctionDebugInfo functionDebugInfo) {
    if (functionDebugInfo == null || functionDebugInfo.IsUnknown) {
      return false;
    }

    if (functionDebugInfo.HasSourceLines) {
      return true; // Already populated.
    }

    if (ManagedSymbolFile == null || hasManagedSymbolFileFailure_) {
      return false; // Previous attempt failed.
    }

    // Locate the managed debug file.
    var options = SymbolSettings != null ? SymbolSettings : CoreSettingsProvider.SymbolSettings;

    if (File.Exists(ManagedSymbolFile.FileName)) {
      options.InsertSymbolPath(ManagedSymbolFile.FileName);
    }

    string symbolSearchPath = PDBDebugInfoProvider.ConstructSymbolSearchPath(options);

    using var logWriter = new StringWriter();
    using var symbolReader = new SymbolReader(logWriter, symbolSearchPath);
    symbolReader.SecurityCheck += s => true; // Allow symbols from "unsafe" locations.
    string debugFile =
      symbolReader.FindSymbolFilePath(ManagedSymbolFile.FileName, ManagedSymbolFile.Id, ManagedSymbolFile.Age);

    Trace.WriteLine($">> TraceEvent FindSymbolFilePath for {ManagedSymbolFile.FileName}: {debugFile}");
    Trace.IndentLevel = 1;
    Trace.WriteLine(logWriter.ToString());
    Trace.IndentLevel = 0;
    Trace.WriteLine("<< TraceEvent");

    if (!File.Exists(debugFile)) {
      // Don't try again if PDB not found.
      hasManagedSymbolFileFailure_ = true;
      return false;
    }

    lock (functionDebugInfo) {
      if (!methodILNativeMap_.TryGetValue(functionDebugInfo, out var ilOffsets)) {
        return false;
      }

      try {
        var pdb = symbolReader.OpenSymbolFile(debugFile);

        if (pdb == null) {
          hasManagedSymbolFileFailure_ = true;
          return false;
        }

        // Find the source lines and native code offset mapping for each IL offset.
        foreach (var pair in ilOffsets) {
          var sourceLoc = pdb.SourceLocationForManagedCode((uint)functionDebugInfo.Id, pair.ILOffset);

          if (sourceLoc != null) {
            if (sourceLoc.SourceFile != null && functionDebugInfo.SourceFileName == null) {
              functionDebugInfo.SourceFileName = sourceLoc.SourceFile.GetSourceFile();
              functionDebugInfo.OriginalSourceFileName ??= sourceLoc.SourceFile.BuildTimeFilePath;
            }

            //? TODO: Remove SourceFileName from SourceLineDebugInfo
            var lineInfo = new SourceLineDebugInfo(pair.NativeOffset, sourceLoc.LineNumber,
                                                   sourceLoc.ColumnNumber, functionDebugInfo.SourceFileName);
            functionDebugInfo.AddSourceLine(lineInfo);
          }
        }
      }
      catch (Exception ex) {
        Trace.TraceError($"Failed to read managed PDB from {debugFile}: {ex.Message}\n{ex.StackTrace}");
        hasManagedSymbolFileFailure_ = true;
      }

      return functionDebugInfo.HasSourceLines;
    }
  }

  private class ManagedProcessCode {
    public int ProcessId { get; set; }
    public int MachineType { get; set; }
    public List<MethodCode> Methods { get; set; }
  }
}
