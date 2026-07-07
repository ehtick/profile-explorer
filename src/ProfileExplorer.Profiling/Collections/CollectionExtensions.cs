// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System.Collections.Generic;

namespace ProfileExplorer.Profiling.Collections;

// Small dictionary projection helpers ported into the library so call sites can
// materialize entries/keys/values without depending on ProfileExplorerCore utilities.
public static class CollectionExtensions {
  public static List<(K Key, V Value)> ToList<K, V>(this IDictionary<K, V> dict) {
    var list = new List<(K, V)>(dict.Count);

    foreach (var item in dict) {
      list.Add((item.Key, item.Value));
    }

    return list;
  }

  public static List<K> ToKeyList<K, V>(this IDictionary<K, V> dict) {
    var list = new List<K>(dict.Count);

    foreach (var item in dict) {
      list.Add(item.Key);
    }

    return list;
  }

  public static List<V> ToValueList<K, V>(this IDictionary<K, V> dict) {
    var list = new List<V>(dict.Count);

    foreach (var item in dict) {
      list.Add(item.Value);
    }

    return list;
  }
}
