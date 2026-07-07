// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Core.Profile.CallTree;
using ProfileExplorer.Core.Profile.Data;
using ProfileExplorer.Profiling.Disassembly;
using ProfileExplorer.Profiling.Profiling;
using ProfileExplorer.Profiling.Symbols;

namespace ProfileExplorer.Profiling;

/// <summary>
/// Main entry point for function-level CPU profiling with disassembly annotation.
/// Consumes CPU samples from any source (DataLayer, TraceEvent, etc.) via <see cref="IProfileSample"/>,
/// resolves symbols via its own PDB reader, and produces per-function/per-instruction profiles
/// with optional annotated disassembly.
/// </summary>
public class FunctionProfiler : IDisposable {
  private readonly ProfilerOptions options_;
  private readonly ISymbolFileLocator symbolResolver_;
  private readonly bool ownsSymbolResolver_;
  private readonly IpResolver ipResolver_;
  private readonly SampleAggregator sampleAggregator_;
  private readonly CallTreeBuilder callTreeBuilder_;
  private readonly CounterAggregator? counterAggregator_;
  private readonly ManagedMethodResolver? managedResolver_;

  private readonly Dictionary<string, IProfileImage> imagesByModule_ = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, ISymbolDebugInfo> debugInfoByModule_ = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, string> pdbPathByModule_ = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, string> binaryPathByModule_ = new(StringComparer.OrdinalIgnoreCase);

  private ProfileReport? cachedReport_;
  private bool symbolsLoaded_;

  public FunctionProfiler(ProfilerOptions options)
    : this(options, symbolResolver: null) {
  }

  /// <summary>
  /// Creates a profiler with a custom symbol/binary resolver. When <paramref name="symbolResolver"/>
  /// is <c>null</c>, the library's vendored <see cref="SymbolServerClient"/> is used (and owned/disposed
  /// by this instance). When a resolver is supplied, the caller retains ownership of its lifetime.
  /// </summary>
  public FunctionProfiler(ProfilerOptions options, ISymbolFileLocator? symbolResolver) {
    options.Validate();
    options_ = options;

    if (symbolResolver != null) {
      symbolResolver_ = symbolResolver;
      ownsSymbolResolver_ = false;
    }
    else {
      symbolResolver_ = new SymbolServerClient(options);
      ownsSymbolResolver_ = true;
    }

    managedResolver_ = options.IncludeManagedCode ? new ManagedMethodResolver() : null;
    ipResolver_ = new IpResolver(managedResolver_);
    sampleAggregator_ = new SampleAggregator(ipResolver_);
    callTreeBuilder_ = new CallTreeBuilder(ipResolver_);
    counterAggregator_ = options.IncludePerformanceCounters ? new CounterAggregator(ipResolver_) : null;
  }

  /// <summary>
  /// Register loaded images (modules) with their PDB identity for symbol resolution.
  /// </summary>
  public void AddImages(IEnumerable<IProfileImage> images) {
    foreach (var image in images) {
      string key = image.ImageName;
      imagesByModule_[key] = image;
      ipResolver_.AddImage(key, image.BaseAddress, image.Size);
    }

    InvalidateReport();
  }

  /// <summary>
  /// Add CPU samples. Can be called multiple times (e.g., per-processor batches).
  /// </summary>
  public void AddSamples(IEnumerable<IProfileSample> samples) {
    var sampleList = samples as IReadOnlyList<IProfileSample> ?? samples.ToList();
    sampleAggregator_.AddSamples(sampleList);
    callTreeBuilder_.AddSamples(sampleList);
    InvalidateReport();
  }

  /// <summary>
  /// Add hardware performance counter events (PMU/PMC).
  /// Only processed if <see cref="ProfilerOptions.IncludePerformanceCounters"/> is true.
  /// </summary>
  public void AddPerformanceCounterEvents(IEnumerable<IPerformanceCounterEvent> events) {
    counterAggregator_?.AddEvents(events);
    InvalidateReport();
  }

  /// <summary>
  /// Register managed/.NET method mappings (from CLR JIT events).
  /// Only processed if <see cref="ProfilerOptions.IncludeManagedCode"/> is true.
  /// </summary>
  public void AddManagedMethods(IEnumerable<IManagedMethodMapping> methods) {
    if (managedResolver_ == null) return;

    foreach (var method in methods) {
      managedResolver_.AddMethod(method);
    }

    InvalidateReport();
  }

  /// <summary>
  /// Load symbols for all registered images. Downloads PDBs from the symbol server.
  /// </summary>
  public async Task LoadSymbolsAsync(CancellationToken ct = default) {
    if (symbolsLoaded_) return;

    foreach (var (moduleName, image) in imagesByModule_) {
      if (image.PdbGuid == Guid.Empty) continue;

      try {
        string pdbName = !string.IsNullOrEmpty(image.PdbName)
          ? Path.GetFileName(image.PdbName)
          : Path.ChangeExtension(image.ImageName, ".pdb");

        string? pdbPath = await symbolResolver_.FindSymbolFileAsync(pdbName, image.PdbGuid, image.PdbAge, ct);
        if (pdbPath == null) continue;

        pdbPathByModule_[moduleName] = pdbPath;

        // Load debug info and register function list with the IP resolver.
        // Cache the enumerated PDB function list to avoid re-enumeration on subsequent loads.
        var provider = new PdbSymbolProvider();
        var cacheKey = new SymbolFileDescriptor(pdbName, image.PdbGuid, image.PdbAge);
        string cacheDir = !string.IsNullOrEmpty(options_.SymbolCacheDirectory)
          ? Path.Combine(options_.SymbolCacheDirectory, "symcache")
          : SymbolFileCache.DefaultCacheDirectoryPath;

        if (provider.LoadDebugInfo(pdbPath, cacheKey, cacheDir)) {
          debugInfoByModule_[moduleName] = provider;
          var sortedFunctions = provider.GetSortedFunctions();
          if (sortedFunctions.Count > 0) {
            ipResolver_.SetFunctions(moduleName, sortedFunctions);
          }
          else {
            Log($"PDB loaded but 0 functions: {moduleName} ({pdbPath})");
          }
        }
        else {
          Log($"PDB load FAILED: {moduleName} - {PdbSymbolProvider.DiaRegistrationError}");
          provider.Dispose();
        }
      }
      catch (Exception) {
        // Symbol loading failure for this module — continue with others.
      }
    }

    symbolsLoaded_ = true;
    InvalidateReport();
  }

  /// <summary>
  /// Invalidate the memoized report so the next <see cref="GetReport"/> call rebuilds it.
  /// Called whenever new data (images, samples, counters, managed methods, symbols) is added.
  /// </summary>
  private void InvalidateReport() {
    cachedReport_ = null;
  }

  /// <summary>
  /// Build the aggregated profiling report (per-function profiles + call tree + totals) from
  /// added samples. Requires StackFrames on <see cref="IProfileSample"/> for the call tree.
  /// </summary>
  public ProfileReport GetReport(
    string? processName = null,
    int? processId = null) {
    if (cachedReport_ != null) return cachedReport_;

    var functions = sampleAggregator_.Build();
    var totalWeight = sampleAggregator_.TotalWeight;

    // Merge per-instruction performance counter data into the function profiles.
    if (counterAggregator_ != null) {
      foreach (var (id, data) in functions) {
        var counters = counterAggregator_.GetCounters(id);
        if (counters != null) {
          data.InstructionCounters = new Dictionary<long, PerformanceCounterValueSet>(counters);
        }
      }
    }

    // Filter by minimum self percent.
    if (options_.MinSelfPercent > 0 && totalWeight.Ticks > 0) {
      double totalMs = totalWeight.TotalMilliseconds;
      var filtered = new Dictionary<ProfileFunctionId, FunctionProfileData>();

      foreach (var (id, data) in functions) {
        double exclusivePercent = data.ExclusiveWeight.TotalMilliseconds / totalMs * 100;

        if (exclusivePercent >= options_.MinSelfPercent) {
          filtered[id] = data;
        }
      }

      functions = filtered;
    }

    var callTree = callTreeBuilder_.Build();
    cachedReport_ = new ProfileReport(functions, callTree, totalWeight);
    return cachedReport_;
  }

  /// <summary>
  /// Get annotated disassembly for a specific function.
  /// Downloads the binary on-demand, disassembles via Capstone, and annotates with timing data.
  /// </summary>
  public async Task<AnnotatedAssembly?> GetAnnotatedAssemblyAsync(
    ProfileFunctionId functionId,
    FunctionProfileData function,
    CancellationToken ct = default) {
    string moduleName = functionId.ModuleName;
    long functionRva = function.FunctionDebugInfo.RVA;
    int functionSize = (int)function.FunctionDebugInfo.Size;

    // Download binary if not already cached.
    if (!binaryPathByModule_.TryGetValue(moduleName, out var binaryPath)) {
      if (imagesByModule_.TryGetValue(moduleName, out var image)) {
        binaryPath = await symbolResolver_.FindBinaryFileAsync(
          image.ImageName, image.TimeDateStamp, image.Size, ct);

        if (binaryPath != null) {
          binaryPathByModule_[moduleName] = binaryPath;
        }
      }
    }

    if (binaryPath == null) {
      // Binary not available — try to return hot lines from instruction weights
      // without disassembly. Avoids DIA COM calls (AccessViolationException risk).
      Log($"Binary not found for {moduleName}, falling back to instruction weights ({function.InstructionWeight.Count} offsets, {function.ExclusiveWeight.TotalMilliseconds:F1}ms)");
      try {
        var result = GetHotLinesWithoutBinary(function, functionRva);
        Log($"GetHotLinesWithoutBinary: {(result != null ? $"{result.HotLines.Count} hot lines" : "null")}");
        return result;
      }
      catch (Exception ex) {
        Log($"GetHotLinesWithoutBinary failed for {functionId}: {ex.GetType().Name}: {ex.Message}");
        return null;
      }
    }

    // Get debug info for source line + call-target annotation.
    debugInfoByModule_.TryGetValue(moduleName, out var debugInfoProvider);

    // Disassemble using the mature capstone disassembler (reads architecture and image base
    // from the PE binary itself). Resolves call/jump targets via the debug info provider.
    using var disassembler = Disassembler.CreateForBinary(binaryPath, debugInfoProvider, null);

    if (disassembler == null) return null;

    var instructions = disassembler.DisassembleToList(functionRva, functionSize);

    if (instructions.Count == 0) return null;

    FunctionDebugInfo? funcDebugInfo = debugInfoProvider?.FindFunctionByRVA(functionRva) ?? function.FunctionDebugInfo;

    // Annotate.
    return AssemblyAnnotator.Annotate(
      instructions,
      function.InstructionWeight,
      functionRva,
      debugInfoProvider,
      funcDebugInfo,
      options_.Architecture,
      options_.MinHotLinePercent,
      options_.MaxHotLines);
  }

  public void Dispose() {
    if (ownsSymbolResolver_ && symbolResolver_ is IDisposable disposableResolver) {
      disposableResolver.Dispose();
    }

    foreach (var (_, provider) in debugInfoByModule_) {
      provider.Dispose();
    }

    debugInfoByModule_.Clear();
  }

  // Forward a diagnostic message to the consumer-provided sink (no-op when none is configured).
  private void Log(string message) => options_.LogCallback?.Invoke(message);

  /// <summary>
  /// Generate hot lines from instruction weights only, without requiring
  /// the binary for Capstone disassembly. Avoids DIA COM calls to prevent
  /// AccessViolationException from cross-thread COM access.
  /// Uses the function's debug-info source file name if available.
  /// </summary>
  private AnnotatedAssembly? GetHotLinesWithoutBinary(FunctionProfileData function, long functionRva) {
    if (function.InstructionWeight.Count == 0) return null;

    var totalWeight = function.InstructionWeight.Values.Aggregate(TimeSpan.Zero, (sum, w) => sum + w);
    var hotLines = new List<HotLine>();
    var lines = new List<AssemblyLine>();
    var sb = new System.Text.StringBuilder();

    // Use source file from the function's debug info if available.
    string? sourceFile = function.FunctionDebugInfo.SourceFileName;

    foreach (var (offset, weight) in function.InstructionWeight.OrderByDescending(kv => kv.Value)) {
      double percent = totalWeight > TimeSpan.Zero
        ? weight.TotalMilliseconds / totalWeight.TotalMilliseconds * 100 : 0;

      if (percent < options_.MinHotLinePercent) continue;

      string text = $"[offset +0x{offset:X}]";

      var line = new AssemblyLine(
        address: functionRva + offset,
        rva: functionRva + offset,
        instructionText: text,
        weight: weight,
        percent: percent,
        sourceFile: sourceFile,
        sourceLine: null);
      lines.Add(line);

      sb.AppendLine($"{functionRva + offset:X}:    {text}    [Time(%): {percent:F2}%, Time: {weight.TotalMilliseconds:F2} ms]");

      hotLines.Add(new HotLine(
        instructionOffset: offset,
        percent: percent,
        time: weight,
        instructionText: text,
        sourceFile: sourceFile,
        sourceLine: null));

      if (hotLines.Count >= options_.MaxHotLines) break;
    }

    if (hotLines.Count == 0) return null;

    return new AnnotatedAssembly(sb.ToString(), lines, hotLines);
  }
}
