// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using ProfileExplorer.Core.Profile;

namespace ProfileExplorer.Core.Profile.CallTree;

/// <summary>
/// IRTextFunction-accepting convenience overloads for <see cref="ProfileCallTree"/> query methods.
/// These live on the PE side as extensions so <see cref="ProfileCallTree"/> itself stays
/// IRTextFunction-free (and movable to the library), while existing UI call sites that hold an
/// <c>IRTextFunction</c> keep working by converting to the neutral <see cref="ProfileFunctionId"/>.
/// </summary>
public static class ProfileCallTreeExtensions {
  public static List<ProfileCallTreeNode> GetCallTreeNodes(this ProfileCallTree tree, IRTextFunction function) {
    return tree.GetCallTreeNodes(function.ToProfileId());
  }

  public static List<ProfileCallTreeNode> GetSortedCallTreeNodes(this ProfileCallTree tree, IRTextFunction function) {
    return tree.GetSortedCallTreeNodes(function.ToProfileId());
  }

  public static ProfileCallTreeNode GetCombinedCallTreeNode(this ProfileCallTree tree, IRTextFunction function,
                                                            ProfileCallTreeNode parentNode = null) {
    return tree.GetCombinedCallTreeNode(function.ToProfileId(), parentNode);
  }

  public static TimeSpan GetCombinedCallTreeNodeWeight(this ProfileCallTree tree, IRTextFunction function) {
    return tree.GetCombinedCallTreeNodeWeight(function.ToProfileId());
  }
}
