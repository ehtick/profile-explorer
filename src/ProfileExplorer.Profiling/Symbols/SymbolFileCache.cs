// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ProtoBuf;

namespace ProfileExplorer.Core.Binary;

[ProtoContract]
public class SymbolFileCache {
  private static int CurrentFileVersion = 3;
  private static int MinSupportedFileVersion = 3;
  [ProtoMember(1)]
  public int Version { get; set; }
  [ProtoMember(2)]
  public SymbolFileDescriptor SymbolFile { get; set; }
  [ProtoMember(3)]
  public List<FunctionDebugInfo> FunctionList { get; set; }
  public static string DefaultCacheDirectoryPath => Path.Combine(Path.GetTempPath(), "ProfileExplorer", "symcache");

  public static Task<bool> SerializeAsync(SymbolFileCache symCache, string directoryPath) {
    try {
      symCache.Version = CurrentFileVersion;

      if (!Directory.Exists(directoryPath)) {
        Directory.CreateDirectory(directoryPath);
      }

      string cachePath = Path.Combine(directoryPath, MakeCacheFilePath(symCache.SymbolFile));

      using var fileStream = File.Create(cachePath);
      using var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest);
      Serializer.Serialize(gzipStream, symCache);
      return Task.FromResult(true);
    }
    catch (Exception ex) {
      Trace.WriteLine($"Failed to save symbol file cache: {ex.Message}");
      return Task.FromResult(false);
    }
  }

  public static Task<SymbolFileCache> DeserializeAsync(SymbolFileDescriptor symbolFile, string directoryPath) {
    try {
      string cachePath = Path.Combine(directoryPath, MakeCacheFilePath(symbolFile));

      if (!File.Exists(cachePath)) {
        return Task.FromResult<SymbolFileCache>(null);
      }

      using var fileStream = File.OpenRead(cachePath);
      using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
      var symCache = Serializer.Deserialize<SymbolFileCache>(gzipStream);

      if (symCache.Version < MinSupportedFileVersion) {
        Trace.WriteLine($"File version mismatch in deserialized symbol file cache");
        Trace.WriteLine($"  actual: {symCache.Version} vs min supported {MinSupportedFileVersion}");
        return Task.FromResult<SymbolFileCache>(null);
      }

      // Ensure it's a cache for the same symbol file.
      if (symCache.SymbolFile.Equals(symbolFile)) {
        return Task.FromResult(symCache);
      }

      Trace.WriteLine($"Symbol file mismatch in deserialized symbol file cache");
      Trace.WriteLine($"  actual: {symCache.SymbolFile} vs expected {symbolFile}");
    }
    catch (Exception ex) {
      Trace.WriteLine($"Failed to load symbol file cache: {ex.Message}");
    }

    return Task.FromResult<SymbolFileCache>(null);
  }

  private static string MakeCacheFilePath(SymbolFileDescriptor symbolFile) {
    string name = string.IsNullOrEmpty(symbolFile.FileName) ? "" : Path.GetFileName(symbolFile.FileName);
    return $"{name}-{symbolFile.Id}-{symbolFile.Age}.cache";
  }
}