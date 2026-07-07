// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Text;
using ProfileExplorer.Core.Utilities;

namespace ProfileExplorer.Core.IR.Tags;

public sealed class SourceStackTrace {
  public SourceStackTrace() {
    Frames = new List<SourceStackFrame>();
  }

  public SourceStackTrace(IEnumerable<SourceStackFrame> frames) : this() {
    AddFrames(frames);
  }

  public List<SourceStackFrame> Frames { get; set; }
  public byte[] Signature { get; set; }

  public void AddFrames(IEnumerable<SourceStackFrame> frames) {
    Frames.AddRange(frames);
    UpdateSignature();
  }

  public override bool Equals(object obj) {
    return obj is SourceStackTrace trace &&
           EqualityComparer<byte[]>.Default.Equals(Signature, trace.Signature);
  }

  public override int GetHashCode() {
    return HashCode.Combine(Signature);
  }

  public void UpdateSignature() {
    // Compute a hash that identifies the stack trace
    // to speed up equality check.
    var bytesList = new List<byte[]>(Frames.Count);

    foreach (var frame in Frames) {
      bytesList.Add(Encoding.UTF8.GetBytes(frame.Function));
      bytesList.Add(Encoding.UTF8.GetBytes(frame.FilePath));
      bytesList.Add(BitConverter.GetBytes(frame.Line));
      bytesList.Add(BitConverter.GetBytes(frame.Column));
    }

    Signature = CompressionUtils.CreateSHA256(bytesList);
  }
}