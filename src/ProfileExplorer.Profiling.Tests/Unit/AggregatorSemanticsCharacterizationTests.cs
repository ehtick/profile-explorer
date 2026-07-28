// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core.Binary;      // FunctionDebugInfo
using ProfileExplorer.Core.Profile;      // ProfileFunctionId
using ProfileExplorer.Core.Profile.Data; // FunctionProfileData
using ProfileExplorer.Profiling.Profiling; // IpResolver, SampleAggregator (InternalsVisibleTo)
using ProfileExplorer.Profiling.Tests.Helpers; // SyntheticSample

namespace ProfileExplorer.Profiling.Tests.Unit;

/// <summary>
/// Verifies the library aggregator reproduces Core's per-instruction attribution, so Core can
/// delegate to the library without changing observable behavior (library = single source of truth).
///
/// Scenario: one CPU sample taken inside <c>Leaf</c> (offset 0x10), whose stack shows it was called
/// from <c>Caller</c> (return address at offset 0x20). Both engines credit a per-instruction sample
/// at the caller's call-site offset (0x20) for EVERY unique function on the stack — this is what
/// makes a `call` instruction show the inclusive time of what it invoked.
///
///   * Core  (FunctionProfileProcessor.ProcessSample): credits AddInstructionSample per unique frame.
///   * Library (SampleAggregator.AddSamples): reconciled to do the same for caller frames.
///
/// The library side below runs the REAL <see cref="SampleAggregator"/>. The Core side is a
/// line-for-line transcription of FunctionProfileProcessor.ProcessSample
/// (src/ProfileExplorerCore/Profile/Processing/FunctionProfileProcessor.cs, the per-frame loop),
/// using the REAL <see cref="FunctionProfileData"/> type and the same AddInstructionSample/Weight/
/// ExclusiveWeight operations, so the comparison is faithful without standing up the full ETW
/// resolved-stack machinery.
/// </summary>
[TestClass]
public class AggregatorSemanticsCharacterizationTests {
  private const string Module = "app.dll";
  private const long ModuleBase = 0x10000;
  private const int ModuleSize = 0x10000;
  private const long LeafRva = 0x1000;
  private const long CallerRva = 0x2000;
  private const long LeafOffset = 0x10;   // sample IP within Leaf
  private const long CallerOffset = 0x20; // return address within Caller

  public TestContext TestContext { get; set; }

  [TestMethod]
  [TestCategory("Characterization")]
  public void CallerInstructionAttribution_LibraryVsCore_Matches() {
    var weight = TimeSpan.FromMilliseconds(1);
    long leafIp = ModuleBase + LeafRva + LeafOffset;      // 0x11010
    long callerReturnIp = ModuleBase + CallerRva + CallerOffset; // 0x12020

    var leafId = new ProfileFunctionId(Module, "Leaf");
    var callerId = new ProfileFunctionId(Module, "Caller");

    // ---- LIBRARY: real SampleAggregator over a leaf-first stack [leaf, caller-return] ----
    var ipResolver = new IpResolver();
    ipResolver.AddImage(Module, ModuleBase, ModuleSize);
    ipResolver.SetFunctions(Module, new List<FunctionDebugInfo> {
      new FunctionDebugInfo("Leaf", LeafRva, 0x100),
      new FunctionDebugInfo("Caller", CallerRva, 0x100)
    });

    var aggregator = new SampleAggregator(ipResolver);
    var sample = new SyntheticSample(leafIp, weight, 1, 1, Module, ModuleBase,
                                     new long[] { leafIp, callerReturnIp });
    aggregator.AddSamples(new[] { sample });
    var lib = aggregator.Build();
    var libLeaf = lib[leafId];
    var libCaller = lib[callerId];

    // ---- CORE: faithful transcription of FunctionProfileProcessor.ProcessSample ----
    var core = CoreProcessSampleTranscription(weight, new[] {
      // leaf-first: (funcId, funcRva, frameRva=module-relative RVA of the frame's IP, debugInfo)
      (leafId,   LeafRva,   LeafRva + LeafOffset,     new FunctionDebugInfo("Leaf", LeafRva, 0x100)),
      (callerId, CallerRva, CallerRva + CallerOffset, new FunctionDebugInfo("Caller", CallerRva, 0x100))
    });
    var coreLeaf = core[leafId];
    var coreCaller = core[callerId];

    TestContext.WriteLine(Describe("LIBRARY", libLeaf, libCaller));
    TestContext.WriteLine(Describe("CORE   ", coreLeaf, coreCaller));

    // ---- Agreements: exclusive + inclusive weight, and leaf per-instruction attribution ----
    Assert.AreEqual(coreLeaf.ExclusiveWeight, libLeaf.ExclusiveWeight, "leaf exclusive weight should match");
    Assert.AreEqual(coreLeaf.Weight, libLeaf.Weight, "leaf inclusive weight should match");
    Assert.AreEqual(coreCaller.ExclusiveWeight, libCaller.ExclusiveWeight, "caller exclusive weight should match (both 0)");
    Assert.AreEqual(coreCaller.Weight, libCaller.Weight, "caller inclusive weight should match");
    CollectionAssert.AreEquivalent(coreLeaf.InstructionWeight.Keys.ToList(),
                                   libLeaf.InstructionWeight.Keys.ToList(),
                                   "leaf per-instruction offsets should match");

    // ---- Caller per-instruction attribution now matches (reconciled) ----
    Assert.AreEqual(weight, coreCaller.InstructionWeight[CallerOffset],
      "CORE attributes a per-instruction sample at the caller call-site offset 0x20");
    Assert.AreEqual(weight, libCaller.InstructionWeight[CallerOffset],
      "LIBRARY now attributes the same caller call-site sample (behavior-preserving)");
    CollectionAssert.AreEquivalent(coreCaller.InstructionWeight.Keys.ToList(),
                                   libCaller.InstructionWeight.Keys.ToList(),
                                   "caller per-instruction offsets should match");
  }

  /// <summary>
  /// Line-for-line transcription of the per-frame loop in
  /// FunctionProfileProcessor.ProcessSample (Core). Uses the real FunctionProfileData type and the
  /// identical AddInstructionSample / Weight / ExclusiveWeight operations. Frames are leaf-first;
  /// the first frame is the top (leaf) frame.
  /// </summary>
  private static Dictionary<ProfileFunctionId, FunctionProfileData> CoreProcessSampleTranscription(
      TimeSpan weight,
      (ProfileFunctionId funcId, long funcRva, long frameRva, FunctionDebugInfo debugInfo)[] framesLeafFirst) {
    var functionProfiles = new Dictionary<ProfileFunctionId, FunctionProfileData>();
    var stackFunctions = new HashSet<ProfileFunctionId>();
    bool isTopFrame = true;

    foreach (var frame in framesLeafFirst) {
      if (!functionProfiles.TryGetValue(frame.funcId, out var funcProfile)) {
        funcProfile = new FunctionProfileData(frame.debugInfo);
        functionProfiles[frame.funcId] = funcProfile;
      }

      long offset = frame.frameRva - frame.funcRva;

      // Don't count the inclusive time / instruction sample for recursive functions multiple times.
      if (stackFunctions.Add(frame.funcId)) {
        funcProfile.AddInstructionSample(offset, weight);
        funcProfile.Weight += weight;
      }

      // Exclusive time only for the top (leaf) frame.
      if (isTopFrame) {
        funcProfile.ExclusiveWeight += weight;
      }

      isTopFrame = false;
    }

    return functionProfiles;
  }

  private static string Describe(string label, FunctionProfileData leaf, FunctionProfileData caller) {
    string InstrMap(FunctionProfileData d) =>
      d.InstructionWeight.Count == 0
        ? "{}"
        : "{" + string.Join(", ", d.InstructionWeight.OrderBy(p => p.Key)
                                    .Select(p => $"0x{p.Key:X}:{p.Value.TotalMilliseconds}ms")) + "}";

    return $"{label} | Leaf   excl={leaf.ExclusiveWeight.TotalMilliseconds}ms incl={leaf.Weight.TotalMilliseconds}ms instr={InstrMap(leaf)}" +
           $"\n{new string(' ', label.Length)} | Caller excl={caller.ExclusiveWeight.TotalMilliseconds}ms incl={caller.Weight.TotalMilliseconds}ms instr={InstrMap(caller)}";
  }
}
