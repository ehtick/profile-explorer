// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
namespace ProfileExplorer.Profiling;

/// <summary>
/// Describes a loaded module/image with its PDB identity for symbol resolution.
/// Consumers implement this to bridge their image/module source.
/// </summary>
public interface IProfileImage {
  /// <summary>Image file name (e.g., "ntdll.dll").</summary>
  string ImageName { get; }

  /// <summary>Base address where the image is loaded in the process.</summary>
  long BaseAddress { get; }

  /// <summary>Size of the image in bytes.</summary>
  int Size { get; }

  /// <summary>PE TimeDateStamp from the file header (used for binary download).</summary>
  int TimeDateStamp { get; }

  /// <summary>PDB GUID from the CodeView debug directory.</summary>
  Guid PdbGuid { get; }

  /// <summary>PDB Age from the CodeView debug directory.</summary>
  int PdbAge { get; }

  /// <summary>PDB file name (e.g., "ntdll.pdb").</summary>
  string PdbName { get; }

  /// <summary>Process ID this image belongs to.</summary>
  int ProcessId { get; }

  /// <summary>
  /// Full on-disk path of the image as recorded in the trace (e.g., "C:\Windows\System32\ntdll.dll"
  /// or an NT path like "\SystemRoot\System32\ntdll.dll"). Used to disassemble from the local binary
  /// when the capturing machine's OS build matches this one. Optional — defaults to null for sources
  /// (synthetic tests, adapters) that don't carry it.
  /// </summary>
  string? ImagePath => null;
}
