// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Threading.Tasks;
using ProfileExplorer.Core.Providers;
using ProfileExplorer.Core.Utilities;

namespace ProfileExplorer.Core.Binary;

public delegate void DisassemblerProgressHandler(DisassemblerProgress info);

public enum DisassemblerStage {
  Disassembling,
  PostProcessing
}

public interface IDisassembler {
  DisassemberResult Disassemble(string imagePath, ICompilerInfoProvider compilerInfo,
                                DisassemblerProgressHandler progressCallback = null,
                                CancelableTask cancelableTask = null);

  Task<DisassemberResult> DisassembleAsync(string imagePath, ICompilerInfoProvider compilerInfo,
                                           DisassemblerProgressHandler progressCallback = null,
                                           CancelableTask cancelableTask = null);

  bool EnsureDisassemblerAvailable();
}

public class DisassemblerOptions {
  public bool IncludeBytes { get; set; }
}

public class DisassemblerProgress {
  public DisassemblerProgress(DisassemblerStage stage) {
    Stage = stage;
  }

  public DisassemblerStage Stage { get; set; }
  public int Total { get; set; }
  public int Current { get; set; }
}

public class DisassemberResult {
  public DisassemberResult(string disassemblyPath, string debugInfoFilePath) {
    DisassemblyPath = disassemblyPath;
    DebugInfoFilePath = debugInfoFilePath;
  }

  public string DisassemblyPath { get; set; }
  public string DebugInfoFilePath { get; set; }
}
