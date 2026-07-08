// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
namespace ProfileExplorer.Profiling;

/// <summary>
/// Projects the host's sample at <paramref name="index"/> into the library's neutral resolved form.
/// Invoked CONCURRENTLY by <see cref="FunctionProfiler.AddResolvedSamplesParallel"/> worker threads,
/// so implementations MUST be thread-safe. Write the sample's resolved frames (leaf-first, with the
/// host omitting any frame it could not resolve) into <paramref name="frames"/>, which is supplied
/// already cleared. Return <c>false</c> to skip the sample (filtered out, or nothing resolvable).
/// </summary>
/// <param name="index">Sample index in the host's collection.</param>
/// <param name="frames">Destination for the leaf-first resolved frames (pre-cleared, reused per worker).</param>
/// <param name="weight">The sample's CPU weight.</param>
/// <param name="threadId">The sample's thread id (used for per-thread call-tree weights).</param>
public delegate bool ResolvedSampleProjector(int index, List<ResolvedFrame> frames,
                                             out TimeSpan weight, out int threadId);
