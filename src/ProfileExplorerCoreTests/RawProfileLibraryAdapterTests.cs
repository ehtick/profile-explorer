// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Profile.Adapters;
using ProfileExplorer.Core.Profile.Data;
using ProfileExplorer.Profiling;

namespace ProfileExplorer.CoreTests;

/// <summary>
/// Verifies the ETW-&gt;library input adapters (Stage 1 of the profiling-engine deduplication) map
/// Core's <see cref="ProfileImage"/> / <see cref="ProfileSample"/> onto the library's
/// <see cref="IProfileImage"/> / <see cref="IProfileSample"/> abstractions correctly — in particular
/// the per-image PDB identity and the leaf-first stack order the library engine relies on.
/// </summary>
[TestClass]
public class RawProfileLibraryAdapterTests {
  [TestMethod]
  public void ImageAdapter_MapsPdbIdentityAndModuleFields() {
    var pdbGuid = Guid.NewGuid();
    var image = new ProfileImage(@"C:\Windows\System32\ntdll.dll", "ntdll.dll",
                                 baseAddress: 0x7FF800000000, defaultBaseAddress: 0,
                                 size: 0x1F0000, timeStamp: 0x12345678, checksum: 0);
    var symbolFile = new SymbolFileDescriptor("ntdll.pdb", pdbGuid, age: 7);

    IProfileImage adapter = new RawProfileImageAdapter(image, processId: 4321, symbolFile);

    Assert.AreEqual("ntdll.dll", adapter.ImageName);
    Assert.AreEqual(0x7FF800000000L, adapter.BaseAddress);
    Assert.AreEqual(0x1F0000, adapter.Size);
    Assert.AreEqual(0x12345678, adapter.TimeDateStamp);
    Assert.AreEqual(pdbGuid, adapter.PdbGuid);
    Assert.AreEqual(7, adapter.PdbAge);
    Assert.AreEqual("ntdll.pdb", adapter.PdbName);
    Assert.AreEqual(4321, adapter.ProcessId);
  }

  [TestMethod]
  public void ImageAdapter_NullSymbolFile_YieldsEmptyPdbIdentity() {
    var image = new ProfileImage(@"C:\app\app.exe", "app.exe",
                                 baseAddress: 0x140000000, defaultBaseAddress: 0,
                                 size: 0x10000, timeStamp: 0, checksum: 0);

    IProfileImage adapter = new RawProfileImageAdapter(image, processId: 1, symbolFile: null);

    Assert.AreEqual(Guid.Empty, adapter.PdbGuid);
    Assert.AreEqual(0, adapter.PdbAge);
    Assert.AreEqual(string.Empty, adapter.PdbName);
    Assert.AreEqual("app.exe", adapter.ImageName);
  }

  [TestMethod]
  public void SampleAdapter_PassesThroughFields_LeafFirstStack() {
    var frames = new long[] { 0x1000, 0x2000, 0x3000 }; // leaf-first: [leaf, caller, caller-of-caller]
    IProfileSample adapter = new RawProfileSampleAdapter(
      ip: 0x1000, weight: TimeSpan.FromMilliseconds(1), processId: 42, threadId: 7,
      imageName: "app.dll", imageBaseAddress: 0x140000000, stackFrames: frames);

    Assert.AreEqual(0x1000L, adapter.InstructionPointer);
    Assert.AreEqual(TimeSpan.FromMilliseconds(1), adapter.Weight);
    Assert.AreEqual(42, adapter.ProcessId);
    Assert.AreEqual(7, adapter.ThreadId);
    Assert.AreEqual("app.dll", adapter.ImageName);
    Assert.AreEqual(0x140000000L, adapter.ImageBaseAddress);

    Assert.IsNotNull(adapter.StackFrames);
    Assert.AreEqual(3, adapter.StackFrames.Count);
    Assert.AreEqual(0x1000L, adapter.StackFrames[0]); // leaf at index 0 (== sample IP)
    Assert.AreEqual(0x2000L, adapter.StackFrames[1]);
    Assert.AreEqual(0x3000L, adapter.StackFrames[2]);
  }

  [TestMethod]
  public void SampleAdapter_NoStack_ExposesNullStackFrames() {
    IProfileSample adapter = new RawProfileSampleAdapter(
      ip: 0x5000, weight: TimeSpan.FromMilliseconds(1), processId: 1, threadId: 1,
      imageName: "app.dll", imageBaseAddress: 0x140000000, stackFrames: null);

    Assert.IsNull(adapter.StackFrames);
    Assert.AreEqual(0x5000L, adapter.InstructionPointer);
  }
}
