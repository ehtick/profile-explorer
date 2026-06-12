// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Generic;

namespace ProfileExplorer.Core.Binary;

/// <summary>
/// A resolved call target within JIT-compiled managed code (address -> symbol name).
/// </summary>
public struct AddressNamePair {
  public long Address { get; set; }
  public string Name { get; set; }

  public AddressNamePair(long address, string name) {
    Address = address;
    Name = name;
  }
}

/// <summary>
/// JIT-compiled native code for a managed method: the code bytes and resolved call targets.
/// </summary>
public class MethodCode {
  public MethodCode(long address, int size, byte[] code) {
    Address = address;
    Size = size;
    Code = code;
    CallTargets = new List<AddressNamePair>();
  }

  public long Address { get; set; }
  public int Size { get; set; }
  public byte[] Code { get; set; }
  public List<AddressNamePair> CallTargets { get; set; }

  public string FindCallTarget(long address) {
    int index = CallTargets.FindIndex(item => item.Address == address);

    if (index != -1) {
      return CallTargets[index].Name;
    }

    return null;
  }
}
