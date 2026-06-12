// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using ProfileExplorer.Core.IR;
using ProfileExplorer.Core.Providers;
using ProfileExplorer.Core.Settings;
using ProfileExplorer.Core.Utilities;
using ProtoBuf;

namespace ProfileExplorer.Core.Binary;

public interface IDebugInfoProvider : ISymbolDebugInfo {
  public SymbolFileSourceSettings SymbolSettings { get; set; }

  //bool LoadDebugInfo(string debugFilePath, IDebugInfoProvider other = null);
  bool LoadDebugInfo(DebugFileSearchResult debugFile, IDebugInfoProvider other = null);
  bool AnnotateSourceLocations(FunctionIR function, IRTextFunction textFunc);
  bool AnnotateSourceLocations(FunctionIR function, FunctionDebugInfo funcDebugInfo);
  SourceFileDebugInfo FindFunctionSourceFilePath(IRTextFunction textFunc);
}