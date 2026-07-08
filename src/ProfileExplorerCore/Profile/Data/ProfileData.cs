// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile.Adapters;
using ProfileExplorer.Core.Profile.CallTree;
using ProfileExplorer.Core.Profile.Processing;
using ProfileExplorer.Core.Utilities;
using ProfileExplorer.Profiling;

namespace ProfileExplorer.Core.Profile.Data;

public class ProfileData {
  public ProfileData(TimeSpan profileWeight, TimeSpan totalWeight) : this() {
    ProfileWeight = profileWeight;
    TotalWeight = totalWeight;
  }

  public ProfileData() {
    ProfileWeight = TimeSpan.Zero;
    FunctionProfiles = new Dictionary<ProfileFunctionId, FunctionProfileData>();
    FunctionResolver = new Dictionary<ProfileFunctionId, IRTextFunction>();
    ModuleWeights = new Dictionary<int, TimeSpan>();
    PerformanceCounters = new Dictionary<int, PerformanceCounter>();
    ModuleCounters = new Dictionary<string, PerformanceCounterValueSet>();
    Threads = new Dictionary<int, ProfileThread>();
    Modules = new Dictionary<int, ProfileImage>();
    Samples = new List<(ProfileSample, ResolvedProfileStack)>();
    Events = new List<(PerformanceCounterEvent Sample, ResolvedProfileStack Stack)>();
    ModuleDebugInfo = new Dictionary<string, IDebugInfoProvider>();
    Filter = new ProfileSampleFilter();
  }

  public TimeSpan ProfileWeight { get; set; }
  public TimeSpan TotalWeight { get; set; }
  public Dictionary<ProfileFunctionId, FunctionProfileData> FunctionProfiles { get; set; }
  // Maps the neutral function identity back to its IRTextFunction (for UI/document navigation).
  // Populated at the single add-path (GetOrCreateFunctionProfile).
  public Dictionary<ProfileFunctionId, IRTextFunction> FunctionResolver { get; set; }
  public Dictionary<int, TimeSpan> ModuleWeights { get; set; }
  public Dictionary<string, PerformanceCounterValueSet> ModuleCounters { get; set; }
  public Dictionary<int, PerformanceCounter> PerformanceCounters { get; set; }
  public ProfileCallTree CallTree { get; set; }
  public ThreadSampleRanges ThreadSampleRanges { get; set; }
  public ProfileDataReport Report { get; set; }
  public List<(ProfileSample Sample, ResolvedProfileStack Stack)> Samples { get; set; }
  public List<(PerformanceCounterEvent Sample, ResolvedProfileStack Stack)> Events { get; set; }
  public ProfileProcess Process { get; set; }
  public Dictionary<int, ProfileThread> Threads { get; set; }
  public Dictionary<int, ProfileImage> Modules { get; set; }
  public Dictionary<string, IDebugInfoProvider> ModuleDebugInfo { get; set; }
  public ProfileSampleFilter Filter { get; set; }

  public List<PerformanceCounter> SortedPerformanceCounters {
    get {
      var list = PerformanceCounters.ToValueList();
      list.Sort((a, b) => b.Id.CompareTo(a.Id));
      return list;
    }
  }

  public List<(int ThreadId, TimeSpan Weight)> SortedThreadWeights {
    get {
      var list = new List<(int ThreadId, TimeSpan Weight)>();
      var threadWeights = new Dictionary<int, TimeSpan>();
      var sampleSpan = CollectionsMarshal.AsSpan(Samples);

      for (int i = 0; i < sampleSpan.Length; i++) {
        threadWeights.AccumulateValue(sampleSpan[i].Stack.Context.ThreadId,
                                      sampleSpan[i].Sample.Weight);
      }

      foreach ((int threadId, var weight) in threadWeights) {
        list.Add((threadId, weight));
      }

      list.Sort((a, b) => b.Weight.CompareTo(a.Weight));
      return list;
    }
  }

  public void RegisterModuleDebugInfo(string moduleName, IDebugInfoProvider provider) {
    ModuleDebugInfo[moduleName] = provider;
  }

  public void AddModuleSample(int moduleId, TimeSpan weight) {
    ModuleWeights.AccumulateValue(moduleId, weight);
  }

  public void AddModuleCounter(string moduleName, int perfCounterId, long value) {
    if (!ModuleCounters.TryGetValue(moduleName, out var counterSet)) {
      counterSet = new PerformanceCounterValueSet();
      ModuleCounters[moduleName] = counterSet;
    }

    counterSet.AddCounterSample(perfCounterId, value);
  }

  public void RegisterPerformanceCounter(PerformanceCounter perfCounter) {
    perfCounter.Index = PerformanceCounters.Count;
    PerformanceCounters[perfCounter.Id] = perfCounter;
  }

  public PerformanceCounter GetPerformanceCounter(int id) {
    if (PerformanceCounters.TryGetValue(id, out var counter)) {
      return counter;
    }

    return null;
  }

  public PerformanceCounter FindPerformanceCounter(string name) {
    foreach (var pair in PerformanceCounters) {
      if (pair.Value.Name == name) {
        return pair.Value;
      }
    }

    return null;
  }

  public PerformanceMetric RegisterPerformanceMetric(int id, PerformanceMetricConfig config) {
    var baseCounter = FindPerformanceCounter(config.BaseCounterName);
    var relativeCounter = FindPerformanceCounter(config.RelativeCounterName);

    if (baseCounter != null && relativeCounter != null) {
      var metric = new PerformanceMetric(id, config, baseCounter, relativeCounter);
      PerformanceCounters[id] = metric;
      return metric;
    }

    return null;
  }

  public double ScaleFunctionWeight(TimeSpan weight) {
    return ProfileWeight.Ticks == 0 ? 0 : weight.Ticks / (double)ProfileWeight.Ticks;
  }

  public double ScaleModuleWeight(TimeSpan weight) {
    return TotalWeight.Ticks == 0 ? 0 : weight.Ticks / (double)TotalWeight.Ticks;
  }

  public FunctionProfileData GetFunctionProfile(IRTextFunction function) {
    return GetFunctionProfile(Id(function));
  }

  public FunctionProfileData GetFunctionProfile(ProfileFunctionId functionId) {
    return FunctionProfiles.TryGetValue(functionId, out var profile) ? profile : null;
  }

  public bool HasFunctionProfile(IRTextFunction function) {
    return GetFunctionProfile(function) != null;
  }

  public FunctionProfileData GetOrCreateFunctionProfile(IRTextFunction function,
                                                        FunctionDebugInfo debugInfo) {
    var id = Id(function);
    ref var funcProfile =
      ref CollectionsMarshal.GetValueRefOrAddDefault(FunctionProfiles, id, out bool exists);

    if (!exists) {
      funcProfile = new FunctionProfileData(debugInfo);
      FunctionResolver[id] = function; // Remember the IRTextFunction for navigation/resolution.
    }

    return funcProfile;
  }

  public IRTextFunction ResolveFunction(ProfileFunctionId functionId) {
    return FunctionResolver.GetValueOrNull(functionId);
  }

  /// <summary>
  /// Registers the neutral identity -> IRTextFunction mapping used for UI/document navigation.
  /// Call single-threaded (FunctionResolver is a plain dictionary). Idempotent.
  /// </summary>
  public void RegisterFunction(IRTextFunction function) {
    if (function != null) {
      FunctionResolver[Id(function)] = function;
    }
  }

  public List<(IRTextFunction, FunctionProfileData)> GetSortedFunctions() {
    var list = new List<(IRTextFunction, FunctionProfileData)>(FunctionProfiles.Count);

    foreach (var pair in FunctionProfiles) {
      var func = ResolveFunction(pair.Key);

      if (func != null) {
        list.Add((func, pair.Value));
      }
    }

    list.Sort((a, b) => -a.Item2.ExclusiveWeight.CompareTo(b.Item2.ExclusiveWeight));
    return list;
  }

  private static ProfileFunctionId Id(IRTextFunction function) {
    return function != null ? new ProfileFunctionId(function.ModuleName, function.Name) : default;
  }

  public void AddThreads(IEnumerable<ProfileThread> threads) {
    foreach (var thread in threads) {
      Threads[thread.ThreadId] = thread;
    }
  }

  public void AddModules(IEnumerable<ProfileImage> modules) {
    foreach (var module in modules) {
      Modules[module.Id] = module;
    }
  }

  public ProfileThread FindThread(int threadId) {
    if (Threads != null) {
      return Threads.GetValueOrNull(threadId);
    }

    return null;
  }

  public List<int> FindModuleIds(Func<string, bool> matchCheck) {
    var ids = new List<int>();

    foreach (var module in Modules) {
      if (matchCheck(module.Value.ModuleName)) {
        ids.Add(module.Key);
      }
    }

    return ids;
  }

  public TimeSpan FindModulesWeight(Func<string, bool> matchCheck) {
    var ids = FindModuleIds(matchCheck);
    var weight = TimeSpan.Zero;

    foreach (int id in ids) {
      weight += ModuleWeights.GetValueOrDefault(id);
    }

    return weight;
  }

  public ProcessingResult FilterFunctionProfile(ProfileSampleFilter filter) {
    //? TODO: Split ProfileData into a part that has the samples and other info that doesn't change,
    //? while the rest is more like a processing result similar to FuncProfileData
    var currentProfile = new ProcessingResult {
      FunctionProfiles = FunctionProfiles,
      FunctionResolver = FunctionResolver,
      CallTree = CallTree,
      ModuleWeights = ModuleWeights,
      ProfileWeight = ProfileWeight,
      TotalWeight = TotalWeight,
      Filter = Filter
    };

    CallTree?.ResetTags();
    ModuleWeights = new Dictionary<int, TimeSpan>();
    FunctionProfiles = new Dictionary<ProfileFunctionId, FunctionProfileData>();
    FunctionResolver = new Dictionary<ProfileFunctionId, IRTextFunction>();
    ProfileWeight = TimeSpan.Zero;
    TotalWeight = TimeSpan.Zero;

    var profile = ComputeProfile(this, filter);
    ModuleWeights = profile.ModuleWeights;
    ProfileWeight = profile.ProfileWeight;
    TotalWeight = profile.TotalWeight;
    FunctionProfiles = profile.FunctionProfiles;
    FunctionResolver = profile.FunctionResolver;
    CallTree = profile.CallTree;
    Filter = filter;
    return currentProfile;
  }

  public ProcessingResult RestorePreviousProfile(ProcessingResult previousProfile) {
    var currentProfile = new ProcessingResult {
      FunctionProfiles = FunctionProfiles,
      FunctionResolver = FunctionResolver,
      CallTree = CallTree,
      ModuleWeights = ModuleWeights,
      ProfileWeight = ProfileWeight,
      TotalWeight = TotalWeight,
      Filter = Filter
    };

    ModuleWeights = previousProfile.ModuleWeights;
    ProfileWeight = previousProfile.ProfileWeight;
    TotalWeight = previousProfile.TotalWeight;
    FunctionProfiles = previousProfile.FunctionProfiles;
    FunctionResolver = previousProfile.FunctionResolver;
    CallTree = previousProfile.CallTree;
    Filter = previousProfile.Filter;
    return currentProfile;
  }

  public ProfileData ComputeProfile(ProfileData baseProfile, ProfileSampleFilter filter,
                                    bool computeCallTree = true,
                                    int maxChunks = int.MaxValue) {
    // The ProfileExplorer.Profiling library owns aggregation + call-tree building; it runs over the
    // already-resolved sample stacks (Core owns symbol resolution). Managed/JIT/unknown frames flow
    // through because Core resolved them before they landed in ResolvedProfileStack.
    //
    // Aggregation runs in parallel: the library partitions the sample range across workers,
    // aggregates per-function profiles into a shared thread-safe map, and builds per-worker call
    // trees that are merged — matching the parallelism of the former FunctionProfileProcessor /
    // CallTreeProcessor. Core supplies a thread-safe projection of each resolved stack.
    using var profiler = FunctionProfiler.CreateForResolvedInput();

    // Neutral function identity -> IRTextFunction, for UI/document navigation. Populated concurrently
    // from worker threads during projection, so it must be a concurrent map.
    var functionResolver = new ConcurrentDictionary<ProfileFunctionId, IRTextFunction>();
    var instancePaths = BuildInstanceFilterPaths(filter);
    var samples = baseProfile.Samples;

    int startIndex = filter.TimeRange?.StartSampleIndex ?? 0;
    int endIndex = filter.TimeRange?.EndSampleIndex ?? samples.Count;
    int workerCount = maxChunks == int.MaxValue
      ? Math.Max(1, CoreSettingsProvider.GeneralSettings.CurrentCpuCoreLimit)
      : Math.Max(1, maxChunks);

    // Thread-safe projection of one resolved stack into the library's neutral ResolvedFrame form,
    // applying the same thread/instance filtering the sequential path did. Called concurrently.
    bool Project(int index, List<ResolvedFrame> frames, out TimeSpan weight, out int threadId) {
      var entry = samples[index];
      var stack = entry.Stack;
      weight = entry.Sample.Weight;
      threadId = stack.Context.ThreadId;

      if (filter.HasThreadFilter && !filter.ThreadIds.Contains(threadId)) {
        return false;
      }

      if (instancePaths != null && !StackMatchesAnyInstance(stack, instancePaths)) {
        return false;
      }

      foreach (var frame in stack.StackFrames) {
        if (frame.IsUnknown) {
          continue;
        }

        var details = frame.FrameDetails;
        frames.Add(new ResolvedFrame(details.FunctionId, details.DebugInfo, frame.FrameRVA,
                                     details.IsKernelCode, details.IsManagedCode));

        if (details.Function != null) {
          functionResolver.TryAdd(details.FunctionId, details.Function);
        }
      }

      return frames.Count > 0;
    }

    profiler.AddResolvedSamplesParallel(startIndex, endIndex, Project, computeCallTree, workerCount);

    var report = profiler.GetReport();
    var moduleIdByName = BuildModuleIdMap(baseProfile);
    var result = new ProfileData();

    ProfileReportMapper.ApplyReport(result, report,
      id => functionResolver.TryGetValue(id, out var func) ? func : null,
      name => moduleIdByName.GetValueOrDefault(name, 0));

    if (!computeCallTree) {
      result.CallTree = null;
    }

    return result;
  }

  // Builds the per-instance root-first ProfileFunctionId paths used to focus the profile on specific
  // call-tree instances (mirrors the former FunctionProfileProcessor instance filter).
  private static List<List<ProfileFunctionId>> BuildInstanceFilterPaths(ProfileSampleFilter filter) {
    if (filter.FunctionInstances is not { Count: > 0 }) {
      return null;
    }

    var paths = new List<List<ProfileFunctionId>>();

    void AddInstance(ProfileCallTreeNode node) {
      var path = new List<ProfileFunctionId>();

      while (node != null) {
        path.Add(node.FunctionId);
        node = node.Caller;
      }

      path.Reverse(); // root-first
      paths.Add(path);
    }

    foreach (var instance in filter.FunctionInstances) {
      if (instance is ProfileCallTreeGroupNode groupNode) {
        foreach (var node in groupNode.Nodes) {
          AddInstance(node);
        }
      }
      else {
        AddInstance(instance);
      }
    }

    return paths;
  }

  // True if the stack (leaf-first) begins from the root with any instance path (root-first).
  private static bool StackMatchesAnyInstance(ResolvedProfileStack stack,
                                              List<List<ProfileFunctionId>> instancePaths) {
    foreach (var path in instancePaths) {
      if (stack.FrameCount < path.Count) {
        continue;
      }

      bool isMatch = true;

      for (int i = 0; i < path.Count; i++) {
        if (path[i] != stack.StackFrames[stack.FrameCount - i - 1].FrameDetails.FunctionId) {
          isMatch = false;
          break;
        }
      }

      if (isMatch) {
        return true;
      }
    }

    return false;
  }

  // Maps a module name to a representative ProfileImage.Id for ModuleWeights keying.
  private static Dictionary<string, int> BuildModuleIdMap(ProfileData baseProfile) {
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    foreach (var pair in baseProfile.Modules) {
      string name = pair.Value.ModuleName ?? string.Empty;
      map.TryAdd(name, pair.Key);
    }

    return map;
  }

  //? TODO: Port to ProfileSampleProcessor
  public ThreadSampleRanges ComputeThreadSampleRanges() {
    // Compute lists of contiguous range of samples running on the same thread,
    // used later to speed up the timeline slice computation and per-thread filtering.
    var threadSampleRanges = new Dictionary<int, List<ThreadSampleRange>>();

    int sampleIndex = 0;
    int prevThreadId = -1;
    int prevSampleIndex = -1;
    var sampleSpan = CollectionsMarshal.AsSpan(Samples);

    for (int i = 0; i < sampleSpan.Length; i++) {
      int threadId = sampleSpan[i].Stack.Context.ThreadId;

      if (threadId != prevThreadId) {
        if (prevThreadId != -1) {
          threadSampleRanges.GetOrAddValue(prevThreadId).Add(new ThreadSampleRange {
            StartIndex = prevSampleIndex,
            EndIndex = sampleIndex
          });
        }

        prevThreadId = threadId;
        prevSampleIndex = sampleIndex;
      }

      sampleIndex++;
    }

    if (prevThreadId != -1) {
      threadSampleRanges.GetOrAddValue(prevThreadId).Add(new ThreadSampleRange {
        StartIndex = prevSampleIndex,
        EndIndex = sampleIndex
      });
    }

    // Add an entry representing all threads, covering all samples.
    threadSampleRanges[-1] = new List<ThreadSampleRange> {
      new() {
        StartIndex = 0,
        EndIndex = sampleIndex
      }
    };

    ThreadSampleRanges = new ThreadSampleRanges(threadSampleRanges);
    return ThreadSampleRanges;
  }

  public class ProcessingResult {
    public ProfileSampleFilter Filter { get; set; }
    public Dictionary<ProfileFunctionId, FunctionProfileData> FunctionProfiles { get; set; }
    public Dictionary<ProfileFunctionId, IRTextFunction> FunctionResolver { get; set; }
    public ProfileCallTree CallTree { get; set; }
    public Dictionary<int, TimeSpan> ModuleWeights { get; set; }
    public TimeSpan ProfileWeight { get; set; }
    public TimeSpan TotalWeight { get; set; }

    public override string ToString() {
      return $"ProfileWeight: {ProfileWeight}, TotalWeight: {TotalWeight}, " +
             $"FunctionProfiles: {FunctionProfiles.Count}, CallTree: {CallTree}";
    }
  }
}

// Represents a contiguous range of samples running on the same thread.
public struct ThreadSampleRange {
  public int StartIndex;
  public int EndIndex;
}

// Represents a set of sample ranges for each thread.
public class ThreadSampleRanges {
  public ThreadSampleRanges(Dictionary<int, List<ThreadSampleRange>> ranges) {
    Ranges = ranges;
  }

  public Dictionary<int, List<ThreadSampleRange>> Ranges { get; set; }
}