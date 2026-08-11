// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Concurrent;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile;
using ProfileExplorer.Profiling.Symbols;

namespace ProfileExplorer.Profiling.Profiling;

/// <summary>
/// Resolves instruction pointers to module/function pairs using registered images and debug info.
/// Shared infrastructure used by SampleAggregator and CounterAggregator.
/// </summary>
internal class IpResolver {
  private readonly SortedList<long, ImageInfo> imagesByBaseAddress_ = [];
  // Per-module function list keyed by the image BASE ADDRESS — the same identity the address resolver
  // uses. Two modules with the same file name loaded at different bases (side-by-side assemblies, or
  // case-only-different DLLs like WinUI "Microsoft.ui.xaml.dll" vs UWP "Microsoft.UI.Xaml.dll") resolve
  // their functions independently, matching the original Core, which keyed per-module function objects.
  private readonly ConcurrentDictionary<long, List<FunctionDebugInfo>> sortedFunctionsByBase_ = new();
  // Per-module debug-info provider paired with its own RVA cache. The provider has a DIA fallback that
  // resolves PGO-split function chunks the contiguous list omits; the cache memoizes results so a
  // repeated return address doesn't re-query DIA. Both live in one entry so "provider present" always
  // implies "cache present" (no parallel-map invariant to violate), and it's a ConcurrentDictionary so
  // reads are lock-free and robust even if registration ever overlaps resolution on worker threads.
  private readonly record struct ModuleResolver(
    ISymbolDebugInfo Provider,
    ConcurrentDictionary<long, FunctionDebugInfo?> Cache);

  private readonly ConcurrentDictionary<long, ModuleResolver> resolversByBase_ = new();
  private readonly object fallbackLock_ = new();
  private readonly ManagedMethodResolver? managedResolver_;

  // OS pointer size (bytes). The host sets PointerSize from the trace's authoritative metadata (ETW
  // SystemConfigCPU / TraceInfo.PointerSize) and that value wins. When the host leaves it unset it is
  // derived from the registered image address space as a fallback (cached; invalidated on AddImage).
  private int explicitPointerSize_; // caller-supplied; 0 = not set, fall back to derivation
  private int derivedPointerSize_;  // cached image-base derivation; 0 = recompute

  public IpResolver(ManagedMethodResolver? managedResolver = null) {
    managedResolver_ = managedResolver;
  }

  /// <summary>
  /// Register a loaded image with its base address.
  /// </summary>
  public void AddImage(string imageName, long baseAddress, int size) {
    imagesByBaseAddress_[baseAddress] = new ImageInfo(imageName, baseAddress, size);
    derivedPointerSize_ = 0; // Invalidate the derived fallback; recomputed on next query.
  }

  /// <summary>
  /// OS pointer size (bytes) used to pick the correct kernel/user address split
  /// (<see cref="ProfileAddress.IsKernelAddress"/>). Prefer setting this from the trace's authoritative
  /// value — ETW <c>SystemConfigCPU</c> / <c>TraceInfo.PointerSize</c>, as Profile Explorer's main path
  /// does; a caller-set value always wins and survives later <see cref="AddImage"/> calls. When the host
  /// does not set it, it is derived from the registered image address space as a fallback: a 64-bit trace
  /// always loads at least one image above 4 GB (ntdll, the kernel, ASLR'd modules), whereas a 32-bit-OS
  /// trace keeps everything below 4 GB (default 8 when no images are registered).
  /// </summary>
  public int PointerSize {
    get {
      if (explicitPointerSize_ != 0) {
        return explicitPointerSize_;
      }

      if (derivedPointerSize_ == 0) {
        bool any64 = false;

        foreach (long baseAddress in imagesByBaseAddress_.Keys) {
          if ((ulong)baseAddress >= 0x1_0000_0000UL) {
            any64 = true;
            break;
          }
        }

        derivedPointerSize_ = any64 || imagesByBaseAddress_.Count == 0 ? 8 : 4;
      }

      return derivedPointerSize_;
    }
    set => explicitPointerSize_ = value;
  }

  /// <summary>
  /// Register sorted function debug info for the image at <paramref name="baseAddress"/>. Optionally
  /// register the debug-info provider so RVAs the contiguous sorted list doesn't cover (PGO-split
  /// function chunks) can be resolved via its DIA-backed <see cref="ISymbolDebugInfo.FindFunctionByRVA"/>
  /// — matching Core. Keyed by base so two same-named modules (different binaries at different bases)
  /// resolve their functions independently.
  /// </summary>
  public void SetFunctions(long baseAddress, List<FunctionDebugInfo> sortedFunctions,
                           ISymbolDebugInfo? debugInfo = null) {
    sortedFunctionsByBase_[baseAddress] = sortedFunctions;

    if (debugInfo != null) {
      resolversByBase_[baseAddress] = new ModuleResolver(debugInfo, new ConcurrentDictionary<long, FunctionDebugInfo?>());
    }
  }

  // Resolve an RVA through the provider (overlapping-aware BinarySearch + DIA fallback for PGO-split
  // chunks), memoizing per module+RVA so the same return address isn't queried repeatedly. Returns
  // whatever provider.FindFunctionByRVA returns (null on a genuine miss), matching Core's
  // ProfileModuleBuilder.GetOrCreateFunction, which uses the result as-is — it does NOT drop
  // empty-named hits, so we don't either. The cache read is lock-free (ConcurrentDictionary), so the
  // warm path — a return address already resolved — never contends even though Resolve runs on
  // parallel worker threads. Only a cold miss takes fallbackLock_: DIA (COM) is not thread-safe, and
  // provider.FindFunctionByRVA touches DIA for the rare split-chunk case (Core calls it unlocked; we
  // guard it), so contention is limited to genuinely new addresses.
  private FunctionDebugInfo? ResolveViaProvider(in ModuleResolver resolver, long moduleRva) {
    if (resolver.Cache.TryGetValue(moduleRva, out var cached)) return cached; // lock-free warm path

    FunctionDebugInfo? func;
    lock (fallbackLock_) {
      // Re-check under the lock so concurrent misses on the same RVA query DIA only once.
      if (resolver.Cache.TryGetValue(moduleRva, out var raced)) return raced;

      func = resolver.Provider.FindFunctionByRVA(moduleRva);
      resolver.Cache[moduleRva] = func;
    }

    return func;
  }

  /// <summary>
  /// Resolve an instruction pointer to a module name and RVA within that module.
  /// </summary>
  public ResolvedIp? Resolve(long ip) {
    // Try managed method resolution first (if enabled).
    if (managedResolver_ != null) {
      var managed = managedResolver_.FindMethod(ip);
      if (managed != null) {
        long rva = ip - managed.NativeStartAddress;
        var managedInfo = new FunctionDebugInfo(managed.MethodName, 0, (uint)managed.NativeSize);
        return new ResolvedIp(managed.ModuleName ?? "[managed]", rva, managed.MethodName, ip, managedInfo,
          rva, managed.NativeSize, true);
      }
    }

    // Find the module that contains this IP.
    var image = FindImage(ip);
    if (image == null) return null;

    long moduleRva = ip - image.BaseAddress;

    // Find the function within the module. Keyed by the image's BASE ADDRESS, so two modules with the
    // same file name (different binaries at different bases) resolve their functions independently.
    if (sortedFunctionsByBase_.TryGetValue(image.BaseAddress, out var functions)) {
      // When the module's debug-info provider is registered, resolve through its overlapping-aware +
      // DIA-backed lookup (matches Core). The plain contiguous BinarySearch returns the wrong function
      // for overlapping/nested symbols (e.g. an assembly label nested inside a larger function)
      // and misses PGO-split chunks entirely, so split functions lose their inclusive time.
      var func = resolversByBase_.TryGetValue(image.BaseAddress, out var resolver)
        ? ResolveViaProvider(resolver, moduleRva)
        : FunctionDebugInfo.BinarySearch(functions, moduleRva);

      if (func != null) {
        return new ResolvedIp(image.Name, func.RVA, func.Name, ip, func,
          moduleRva - func.RVA,
          (int)func.Size);
      }
    }

    // Module found but function not resolved.
    return new ResolvedIp(image.Name, moduleRva, null, ip, new FunctionDebugInfo(null, moduleRva, 0));
  }

  /// <summary>
  /// True when <paramref name="instancePath"/> (root-first function identities) is a prefix of the
  /// sample stack read from the root. <paramref name="framesLeafFirst"/> is leaf-first, so the root
  /// is the last frame. Used for call-tree instance ("focus on this path") filtering — the neutral
  /// equivalent of Core FunctionProfileProcessor's stack-prefix instance filter.
  /// </summary>
  public bool StackHasInstancePrefix(IReadOnlyList<long>? framesLeafFirst,
                                     IReadOnlyList<ProfileFunctionId> instancePath) {
    if (instancePath.Count == 0) return true;
    if (framesLeafFirst is not { Count: > 0 } || framesLeafFirst.Count < instancePath.Count) return false;

    for (int i = 0; i < instancePath.Count; i++) {
      long ip = framesLeafFirst[framesLeafFirst.Count - 1 - i];
      var resolved = Resolve(ip);
      var id = resolved != null
        ? new ProfileFunctionId(resolved.ModuleName, resolved.FunctionName ?? $"<unknown+0x{resolved.Rva:X}>")
        : default;
      if (!id.Equals(instancePath[i])) return false;
    }

    return true;
  }

  /// <summary>
  /// Instance-prefix check for PRE-RESOLVED frames (leaf-first) whose <see cref="ProfileFunctionId"/>
  /// is already known — no IP resolution needed. <paramref name="instancePath"/> is root-first.
  /// </summary>
  public static bool StackHasInstancePrefixResolved(IReadOnlyList<ResolvedFrame> framesLeafFirst,
                                                    IReadOnlyList<ProfileFunctionId> instancePath) {
    if (instancePath.Count == 0) return true;
    if (framesLeafFirst.Count < instancePath.Count) return false;

    for (int i = 0; i < instancePath.Count; i++) {
      if (!framesLeafFirst[framesLeafFirst.Count - 1 - i].FunctionId.Equals(instancePath[i])) {
        return false;
      }
    }

    return true;
  }

  private ImageInfo? FindImage(long ip) {
    // Binary search for the image with the largest base address <= ip.
    var keys = imagesByBaseAddress_.Keys;
    int low = 0;
    int high = keys.Count - 1;
    ImageInfo? best = null;

    while (low <= high) {
      int mid = low + (high - low) / 2;
      long baseAddr = keys[mid];

      if (baseAddr <= ip) {
        var candidate = imagesByBaseAddress_[baseAddr];
        if (ip < baseAddr + candidate.Size) {
          best = candidate;
        }

        low = mid + 1;
      }
      else {
        high = mid - 1;
      }
    }

    return best;
  }

  /// <summary>
  /// Collect the set of module names that the given samples actually touch — the leaf instruction
  /// pointer plus every stack frame — so a caller can load symbols ONLY for sampled modules instead
  /// of every loaded image. Cheap: an in-memory binary search per frame over the registered image
  /// ranges (no PDBs, no I/O). Modules never touched by a sample never need their symbols.
  /// </summary>
  public HashSet<string> CollectTouchedModules(IReadOnlyList<IProfileSample> samples) {
    var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var sample in samples) {
      if (!string.IsNullOrEmpty(sample.ImageName)) {
        touched.Add(sample.ImageName);
      }

      var leaf = FindImage(sample.InstructionPointer);
      if (leaf != null) {
        touched.Add(leaf.Name);
      }

      if (sample.StackFrames != null) {
        foreach (long ip in sample.StackFrames) {
          var image = FindImage(ip);
          if (image != null) {
            touched.Add(image.Name);
          }
        }
      }
    }

    return touched;
  }
}

/// <summary>
/// Result of resolving an instruction pointer.
/// </summary>
internal record ResolvedIp(
  string ModuleName,
  long Rva,
  string? FunctionName,
  long OriginalIp,
  FunctionDebugInfo DebugInfo,
  long InstructionOffset = 0,
  int FunctionSize = 0,
  bool IsManaged = false);

/// <summary>
/// Information about a loaded image/module.
/// </summary>
internal record ImageInfo(string Name, long BaseAddress, int Size);
