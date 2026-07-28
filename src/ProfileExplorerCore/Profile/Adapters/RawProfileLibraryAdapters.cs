// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile.Data;
using ProfileExplorer.Profiling;

namespace ProfileExplorer.Core.Profile.Adapters;

/// <summary>
/// Bridges Core's ETW-derived <see cref="RawProfileData"/> to the library's neutral input
/// abstractions (<see cref="IProfileImage"/> / <see cref="IProfileSample"/>) so the
/// ProfileExplorer.Profiling engine (FunctionProfiler / SampleAggregator) can consume ETW samples
/// directly — letting Core delegate sample aggregation, IP resolution, and symbol reading to the
/// library instead of maintaining its own parallel implementation.
/// </summary>
public static class RawProfileLibraryAdapter {
  /// <summary>
  /// Build library image descriptors for the given processes, attaching the per-image PDB identity
  /// (GUID / Age / name) that Core captured from the trace's ImageID_DBG (RSDS) events.
  /// </summary>
  public static IReadOnlyList<IProfileImage> CreateImages(RawProfileData profile, IReadOnlyList<int> processIds) {
    var images = new List<IProfileImage>();
    var seen = new HashSet<(int ProcessId, long BaseAddress)>();

    foreach (int processId in processIds) {
      var process = profile.GetOrCreateProcess(processId);

      foreach (var image in process.Images(profile)) {
        if (image == null || image.Size <= 0) {
          continue;
        }

        if (!seen.Add((processId, image.BaseAddress))) {
          continue;
        }

        var symbolFile = profile.GetDebugFileForImage(image, processId);
        images.Add(new RawProfileImageAdapter(image, processId, symbolFile));
      }
    }

    return images;
  }

  /// <summary>
  /// Enumerate library samples for the given processes. Lazy — one adapter is yielded per sample.
  /// Stack frames are leaf-first (Core's <see cref="ProfileStack.FramePointers"/>), which matches
  /// the <see cref="IProfileSample.StackFrames"/> contract.
  /// </summary>
  public static IEnumerable<IProfileSample> CreateSamples(RawProfileData profile, IReadOnlyList<int> processIds) {
    var wanted = new HashSet<int>(processIds);

    foreach (var sample in profile.Samples) {
      var context = sample.GetContext(profile);

      if (!wanted.Contains(context.ProcessId)) {
        continue;
      }

      var image = profile.FindImageForIP(sample.IP, context.ProcessId);
      var stack = sample.GetStack(profile);
      long[]? frames = stack.IsUnknown ? null : stack.FramePointers;

      yield return new RawProfileSampleAdapter(
        sample.IP, sample.Weight, context.ProcessId, context.ThreadId,
        image?.ModuleName, image?.BaseAddress ?? 0, frames);
    }
  }
}

/// <summary>
/// <see cref="IProfileImage"/> view over a Core <see cref="ProfileImage"/> plus the PDB identity
/// resolved from the trace's ImageID_DBG events (may be <c>null</c> when the trace lacks RSDS data).
/// </summary>
public sealed class RawProfileImageAdapter : IProfileImage {
  public RawProfileImageAdapter(ProfileImage image, int processId, SymbolFileDescriptor? symbolFile) {
    ImageName = image.ModuleName ?? string.Empty;
    BaseAddress = image.BaseAddress;
    Size = image.Size;
    TimeDateStamp = image.TimeStamp;
    PdbGuid = symbolFile?.Id ?? Guid.Empty;
    PdbAge = symbolFile?.Age ?? 0;
    PdbName = symbolFile?.FileName ?? string.Empty;
    ProcessId = processId;
  }

  public string ImageName { get; }
  public long BaseAddress { get; }
  public int Size { get; }
  public int TimeDateStamp { get; }
  public Guid PdbGuid { get; }
  public int PdbAge { get; }
  public string PdbName { get; }
  public int ProcessId { get; }
}

/// <summary>
/// <see cref="IProfileSample"/> view over a Core <see cref="ProfileSample"/>. Stack frames are the
/// sample's leaf-first <see cref="ProfileStack.FramePointers"/> (frame 0 is the leaf / sample IP).
/// </summary>
public sealed class RawProfileSampleAdapter : IProfileSample {
  private readonly long[]? stackFrames_;

  public RawProfileSampleAdapter(long ip, TimeSpan weight, int processId, int threadId,
                                 string? imageName, long imageBaseAddress, long[]? stackFrames) {
    InstructionPointer = ip;
    Weight = weight;
    ProcessId = processId;
    ThreadId = threadId;
    ImageName = imageName;
    ImageBaseAddress = imageBaseAddress;
    stackFrames_ = stackFrames;
  }

  public long InstructionPointer { get; }
  public TimeSpan Weight { get; }
  public int ProcessId { get; }
  public int ThreadId { get; }
  public string? ImageName { get; }
  public long ImageBaseAddress { get; }
  public IReadOnlyList<long>? StackFrames => stackFrames_;
}
