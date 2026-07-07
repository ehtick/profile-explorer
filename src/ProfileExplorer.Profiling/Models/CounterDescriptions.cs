// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace ProfileExplorer.Profiling;

/// <summary>
/// Describes a registered hardware performance counter source.
/// </summary>
public class PerformanceCounterInfo {
  public PerformanceCounterInfo(int id, string name, long frequency) {
    Id = id;
    Name = name;
    Frequency = frequency;
  }

  public int Id { get; }
  public string Name { get; }
  public long Frequency { get; }
  public int Index { get; set; }
}

/// <summary>
/// A derived metric computed from two base counters (e.g., cache miss rate = misses / references).
/// </summary>
public class PerformanceMetricInfo {
  public PerformanceMetricInfo(string name, string baseCounterName, string relativeCounterName, bool isPercentage) {
    Name = name;
    BaseCounterName = baseCounterName;
    RelativeCounterName = relativeCounterName;
    IsPercentage = isPercentage;
  }

  public string Name { get; }
  public string BaseCounterName { get; }
  public string RelativeCounterName { get; }
  public bool IsPercentage { get; }

  public double ComputeMetric(long baseValue, long relativeValue) {
    if (baseValue == 0) return 0;
    double result = relativeValue / (double)baseValue;
    return IsPercentage ? Math.Min(result, 1.0) : result;
  }
}
