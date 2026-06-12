// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Linq;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.IR;
using ProfileExplorer.Core.IR.Tags;
using ProfileExplorer.Core.Utilities;

namespace ProfileExplorer.Core.Profile.Data;

/// <summary>
/// IR/document-coupled processing for <see cref="FunctionProfileData"/>: maps per-instruction-offset
/// profile data onto IR elements and source lines. Kept on the PE side (as extension methods) so that
/// <see cref="FunctionProfileData"/> itself stays free of the IR subsystem and can live in the library.
/// </summary>
public static class FunctionProfileDataProcessing {
  public static bool TryFindElementForOffset(AssemblyMetadataTag metadataTag, long offset,
                                             ICompilerIRInfo ir,
                                             out IRElement element) {
    var offsetData = ir.InstructionOffsetData;
    int multiplier = offsetData.InitialMultiplier;

    do {
      long candidateOffset = Math.Max(0, offset - multiplier * offsetData.OffsetAdjustIncrement);

      if (metadataTag.OffsetToElementMap.TryGetValue(candidateOffset, out element)) {
        return true;
      }

      ++multiplier;
    } while (multiplier * offsetData.OffsetAdjustIncrement < offsetData.MaxOffsetAdjust);

    return false;
  }

  public static FunctionProcessingResult Process(this FunctionProfileData profile, FunctionIR function,
                                                 ICompilerIRInfo ir) {
    var metadataTag = function.GetTag<AssemblyMetadataTag>();
    bool hasInstrOffsetMetadata = metadataTag != null && metadataTag.OffsetToElementMap.Count > 0;

    if (!hasInstrOffsetMetadata) {
      return null;
    }

    var result = new FunctionProcessingResult(metadataTag.OffsetToElementMap.Count);

    foreach (var pair in profile.InstructionWeight) {
      if (TryFindElementForOffset(metadataTag, pair.Key, ir, out var element)) {
        result.SampledElements.Add((element, pair.Value));
        result.BlockSampledElementsMap.AccumulateValue(element.ParentBlock, pair.Value);
      }
    }

    if (profile.HasPerformanceCounters) {
      foreach (var pair in profile.InstructionCounters) {
        if (TryFindElementForOffset(metadataTag, pair.Key, ir, out var element)) {
          result.CounterElements.Add((element, pair.Value));
        }

        result.FunctionCountersValue.Add(pair.Value);
      }
    }

    result.BlockSampledElements = result.BlockSampledElementsMap.ToList();
    result.SortSampledElements();
    return result;
  }

  public static SourceLineProcessingResult ProcessSourceLines(this FunctionProfileData profile,
                                                              IDebugInfoProvider debugInfo,
                                                              ICompilerIRInfo ir,
                                                              SourceStackFrame inlinee = null) {
    var result = new SourceLineProcessingResult();
    int firstLine = int.MaxValue;
    int lastLine = int.MinValue;
    var offsetData = ir.InstructionOffsetData;

    var firstLineInfo = debugInfo.FindSourceLineByRVA(profile.FunctionDebugInfo.RVA);

    if (!firstLineInfo.IsUnknown) {
      firstLine = firstLineInfo.Line;
    }

    var lastLineInfo = debugInfo.FindSourceLineByRVA(profile.FunctionDebugInfo.EndRVA);

    if (!lastLineInfo.IsUnknown) {
      lastLine = lastLineInfo.Line;
    }

    foreach (var pair in profile.InstructionWeight) {
      long rva = pair.Key + profile.FunctionDebugInfo.RVA - offsetData.InitialMultiplier;
      var lineInfo = debugInfo.FindSourceLineByRVA(rva, inlinee != null);

      if (!lineInfo.IsUnknown) {
        int line = lineInfo.Line;

        if (inlinee != null) {
          // Map the instruction back to the function that got inlined
          // at the call site, if filtering by an inlinee is used.
          var matchingInlinee = lineInfo.FindSameFunctionInlinee(inlinee);

          if (matchingInlinee != null) {
            line = matchingInlinee.Line;
          }
          else {
            continue; // Don't count the instr. if not part of the inlinee.
          }
        }

        result.SourceLineWeight.AccumulateValue(line, pair.Value);
        firstLine = Math.Min(line, firstLine);
        lastLine = Math.Max(line, lastLine);
      }
    }

    if (profile.HasPerformanceCounters) {
      foreach (var pair in profile.InstructionCounters) {
        long rva = pair.Key + profile.FunctionDebugInfo.RVA;
        var lineInfo = debugInfo.FindSourceLineByRVA(rva, inlinee != null);

        if (!lineInfo.IsUnknown) {
          int line = lineInfo.Line;

          if (inlinee != null) {
            // Map the instruction back to the function that got inlined
            // at the call site, if filtering by an inlinee is used.
            var matchingInlinee = lineInfo.FindSameFunctionInlinee(inlinee);

            if (matchingInlinee != null) {
              line = matchingInlinee.Line;
            }
            else {
              continue; // Don't count the instr. if not part of the inlinee.
            }
          }

          result.SourceLineCounters.AccumulateValue(line, pair.Value);
          firstLine = Math.Min(line, firstLine);
          lastLine = Math.Max(line, lastLine);
        }

        result.FunctionCountersValue.Add(pair.Value);
      }
    }

    result.FirstLineIndex = firstLine;
    result.LastLineIndex = lastLine;
    return result;
  }
}

public class FunctionProcessingResult {
  public FunctionProcessingResult(int capacity = 0) {
    SampledElements = new List<(IRElement, TimeSpan)>(capacity);
    BlockSampledElementsMap = new Dictionary<BlockIR, TimeSpan>(capacity);
    BlockSampledElements = new List<(BlockIR, TimeSpan)>();
    CounterElements = new List<(IRElement, PerformanceCounterValueSet)>(capacity);
    FunctionCountersValue = new PerformanceCounterValueSet();
  }

  public List<(IRElement, TimeSpan)> SampledElements { get; set; }
  public Dictionary<BlockIR, TimeSpan> BlockSampledElementsMap { get; set; }
  public List<(BlockIR, TimeSpan)> BlockSampledElements { get; set; }
  public List<(IRElement, PerformanceCounterValueSet)> CounterElements { get; set; }
  public List<(BlockIR, PerformanceCounterValueSet)> BlockCounterElements { get; set; }
  public PerformanceCounterValueSet FunctionCountersValue { get; set; }

  public double ScaleCounterValue(long value, PerformanceCounter counter) {
    long total = FunctionCountersValue.FindCounterValue(counter);
    return total > 0 ? value / (double)total : 0;
  }

  public void SortSampledElements() {
    BlockSampledElements.Sort((a, b) => b.Item2.CompareTo(a.Item2));
    SampledElements.Sort((a, b) => b.Item2.CompareTo(a.Item2));
  }

  public SampledElementsToLineMapping BuildSampledElementsToLineMapping(FunctionProfileData profile,
                                                                        ParsedIRTextSection parsedSection) {
    var elementMap = BuildElementToWeightMap();
    var counterMap = BuildElementToCounterMap();
    var instrToLineMap = new SampledElementsToLineMapping();

    // Build groups of instructions mapping to the same source line,
    // with their associated sampled weight and perf. counters.
    foreach (var instr in parsedSection.Function.AllInstructions) {
      var tag = instr.GetTag<SourceLocationTag>();

      if (tag != null) {
        var weight = elementMap.GetValueOr(instr, TimeSpan.Zero);
        var counters = counterMap.GetValueOrNull(instr);
        var list = instrToLineMap.SampledElements.GetOrAddValue(tag.Line);
        list.Add((instr, (weight, counters)));
      }
    }

    // Sort elements in each line group by text offset.
    foreach (var linePair in instrToLineMap.SampledElements) {
      linePair.Value.Sort((a, b) => a.Item1.TextLocation.CompareTo(b.Item1.TextLocation));
    }

    return instrToLineMap;
  }

  public Dictionary<IRElement, TimeSpan> BuildElementToWeightMap() {
    var map = new Dictionary<IRElement, TimeSpan>();

    foreach (var pair in SampledElements) {
      map[pair.Item1] = pair.Item2;
    }

    return map;
  }

  public Dictionary<IRElement, PerformanceCounterValueSet> BuildElementToCounterMap() {
    var map = new Dictionary<IRElement, PerformanceCounterValueSet>();

    foreach (var pair in CounterElements) {
      map[pair.Item1] = pair.Item2;
    }

    return map;
  }

  // Mapping from a source line number to a list
  // of associated instructions and their weight and/or perf. counters.
  public record SampledElementsToLineMapping(
    Dictionary<int, List<(IRElement Element,
      (TimeSpan Weight, PerformanceCounterValueSet Counters) Profile)>> SampledElements) {
    public SampledElementsToLineMapping() :
      this(new Dictionary<int, List<(IRElement Element,
             (TimeSpan Weight, PerformanceCounterValueSet Counters) Profile)>>()) {
    }
  }
}

public class SourceLineProcessingResult {
  public SourceLineProcessingResult() {
    SourceLineWeight = new Dictionary<int, TimeSpan>();
    SourceLineCounters = new Dictionary<int, PerformanceCounterValueSet>();
    FunctionCountersValue = new PerformanceCounterValueSet();
  }

  public Dictionary<int, TimeSpan> SourceLineWeight { get; set; } // Line number mapping
  public Dictionary<int, PerformanceCounterValueSet> SourceLineCounters { get; set; } // Line number mapping
  public PerformanceCounterValueSet FunctionCountersValue { get; set; }
  public List<(int LineNumber, TimeSpan Weight)> SourceLineWeightList => SourceLineWeight.ToList();
  public int FirstLineIndex { get; set; }
  public int LastLineIndex { get; set; }
}
