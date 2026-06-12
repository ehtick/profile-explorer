// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProfileExplorer.Core.Binary;

namespace ProfileExplorer.Core.Profile.Data;

public class FunctionProfileData {
  public FunctionProfileData() {
    InstructionWeight = new Dictionary<long, TimeSpan>();
    SampleStartIndex = int.MaxValue;
    SampleEndIndex = int.MinValue;
  }

  public FunctionProfileData(FunctionDebugInfo debugInfo) : this() {
    FunctionDebugInfo = debugInfo;
  }

  public TimeSpan Weight { get; set; }
  public TimeSpan ExclusiveWeight { get; set; }
  public Dictionary<long, TimeSpan> InstructionWeight { get; set; } // Instr. offset mapping
  public Dictionary<long, PerformanceCounterValueSet> InstructionCounters { get; set; }
  public FunctionDebugInfo FunctionDebugInfo { get; set; }
  public int SampleStartIndex { get; set; }
  public int SampleEndIndex { get; set; }
  public bool HasPerformanceCounters => InstructionCounters is {Count: > 0};

  public void MergeWith(FunctionProfileData otherData) {
    Weight += otherData.Weight;
    ExclusiveWeight += otherData.ExclusiveWeight;
    SampleStartIndex = Math.Min(SampleStartIndex, otherData.SampleStartIndex);
    SampleEndIndex = Math.Max(SampleEndIndex, otherData.SampleEndIndex);

    foreach (var pair in otherData.InstructionWeight) {
      ref var existingValue =
        ref CollectionsMarshal.GetValueRefOrAddDefault(InstructionWeight, pair.Key, out bool exists);
      existingValue += pair.Value;
    }

    if (otherData.HasPerformanceCounters) {
      InstructionCounters ??= new Dictionary<long, PerformanceCounterValueSet>();

      foreach (var pair in otherData.InstructionCounters) {
        ref var existingValue =
          ref CollectionsMarshal.GetValueRefOrAddDefault(InstructionCounters, pair.Key, out bool exists);

        if (exists) {
          existingValue.Add(pair.Value);
        }
        else {
          existingValue = pair.Value;
        }
      }
    }
  }

  public void AddCounterSample(long instrOffset, int perfCounterId, long value) {
    InstructionCounters ??= new Dictionary<long, PerformanceCounterValueSet>();

    if (!InstructionCounters.TryGetValue(instrOffset, out var counterSet)) {
      counterSet = new PerformanceCounterValueSet();
      InstructionCounters[instrOffset] = counterSet;
    }

    counterSet.AddCounterSample(perfCounterId, value);
  }

  public void AddInstructionSample(long instrOffset, TimeSpan weight) {
    if (InstructionWeight.TryGetValue(instrOffset, out var currentWeight)) {
      InstructionWeight[instrOffset] = currentWeight + weight;
    }
    else {
      InstructionWeight[instrOffset] = weight;
    }
  }

  public double ScaleWeight(TimeSpan weight) {
    return weight.Ticks / (double)Weight.Ticks;
  }

  public PerformanceCounterValueSet ComputeFunctionTotalCounters() {
    var result = new PerformanceCounterValueSet();

    if (HasPerformanceCounters) {
      foreach (var pair in InstructionCounters) {
        result.Add(pair.Value);
      }
    }

    return result;
  }

  public void Reset() {
    Weight = TimeSpan.Zero;
    ExclusiveWeight = TimeSpan.Zero;
    SampleStartIndex = int.MaxValue;
    SampleEndIndex = int.MinValue;
    InstructionWeight?.Clear();
    InstructionCounters?.Clear();
  }
}