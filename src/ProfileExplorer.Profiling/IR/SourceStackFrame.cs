// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;

namespace ProfileExplorer.Core.IR.Tags;

public sealed class SourceStackFrame : IEquatable<SourceStackFrame> {
  public SourceStackFrame(string function, string filePath, int line, int column) {
    Function = function;
    FilePath = filePath;
    Line = line;
    Column = column;
  }

  public string Function { get; set; }
  public string FilePath { get; set; }
  public int Line { get; set; }
  public int Column { get; set; }

  public bool Equals(SourceStackFrame other) {
    if (ReferenceEquals(null, other))
      return false;
    if (ReferenceEquals(this, other))
      return true;
    return Line == other.Line && Column == other.Column &&
           Function.Equals(other.Function, StringComparison.OrdinalIgnoreCase) &&
           FilePath.Equals(other.FilePath, StringComparison.OrdinalIgnoreCase);
  }

  public static bool operator ==(SourceStackFrame left, SourceStackFrame right) {
    return Equals(left, right);
  }

  public static bool operator !=(SourceStackFrame left, SourceStackFrame right) {
    return !Equals(left, right);
  }

  public override bool Equals(object obj) {
    return ReferenceEquals(this, obj) || obj is SourceStackFrame other && Equals(other);
  }

  public override int GetHashCode() {
    return HashCode.Combine(Function, FilePath, Line, Column);
  }

  public bool HasSameFunction(SourceStackFrame inlinee) {
    return Function.Equals(inlinee.Function, StringComparison.OrdinalIgnoreCase) &&
           FilePath.Equals(inlinee.FilePath, StringComparison.OrdinalIgnoreCase);
  }
}
