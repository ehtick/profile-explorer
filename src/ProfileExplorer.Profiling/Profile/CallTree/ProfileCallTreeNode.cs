// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ProfileExplorer.Core.Binary;
using ProfileExplorer.Core.Collections;

namespace ProfileExplorer.Core.Profile.CallTree;

public enum ProfileCallTreeNodeKind {
  Unset = 0,
  NativeUser = 1,
  NativeKernel = 2,
  Managed = 3
}

public class ProfileCallTreeNode : IEquatable<ProfileCallTreeNode> {
  private static readonly object MergedNodeTag = new();
  public int Id { get; set; }
  public ProfileFunctionId FunctionId { get; set; } // Neutral (module, name) identity; decoupled from IRTextFunction.
  public ProfileCallTreeNodeKind Kind { get; set; }
  private TinyList<ProfileCallTreeNode> children_;
  private ProfileCallTreeNode caller_; // Can't be serialized, reconstructed.
  public FunctionDebugInfo FunctionDebugInfo { get; set; }

  //? TODO: Replace Threads dict and CallSites with a TinyDictionary-like data struct
  //? like TinyList, also consider DictionarySlim instead of Dictionary from
  //? https://github.com/dotnet/corefxlab/blob/archive/src/Microsoft.Experimental.Collections/Microsoft/Collections/Extensions/DictionarySlim
  public Dictionary<long, ProfileCallSite> CallSites { get; set; }
  public Dictionary<int, (TimeSpan Weight, TimeSpan ExclusiveWeight)> ThreadWeights { get; set; }
  public TimeSpan Weight { get; set; }
  public TimeSpan ExclusiveWeight { get; set; }
  public object Tag { get; set; }
  public virtual List<ProfileCallTreeNode> Nodes => new() {this};
  public IList<ProfileCallTreeNode> Children => children_;
  public virtual List<ProfileCallTreeNode> Callers => new() {caller_};
#if DEBUG
  public ProfileCallTreeNode Caller =>
    !IsGroup ? caller_ : throw new InvalidOperationException("For group use Callers");
#else
  public ProfileCallTreeNode Caller => caller_;
#endif
  public virtual bool IsGroup => false;
  public bool HasChildren => Children != null && Children.Count > 0;
  public virtual bool HasCallers => caller_ != null;
  public bool HasCallSites => CallSites != null && CallSites.Count > 0;
  public bool HasThreadWeights => ThreadWeights != null && ThreadWeights.Count > 0;
  public bool HasFunction => !FunctionId.IsUnknown;
  public string FunctionName => FunctionId.FunctionName;
  public string ModuleName => FunctionId.ModuleName;

  public double ScaleWeight(TimeSpan relativeWeigth) {
    return relativeWeigth.Ticks / (double)Weight.Ticks;
  }

  public (TimeSpan Weight, TimeSpan ExclusiveWeight) ChildrenWeight {
    get {
      var weight = TimeSpan.Zero;
      var exclusiveWeight = TimeSpan.Zero;

      if (!HasChildren) {
        return (weight, exclusiveWeight);
      }

      foreach (var child in Children) {
        weight += child.Weight;
        exclusiveWeight += child.ExclusiveWeight;
      }

      return (weight, exclusiveWeight);
    }
  }

  protected ProfileCallTreeNode() { }

  public ProfileCallTreeNode(FunctionDebugInfo funcInfo, ProfileFunctionId functionId,
                             List<ProfileCallTreeNode> children = null,
                             ProfileCallTreeNode caller = null,
                             Dictionary<long, ProfileCallSite> callSites = null,
                             Dictionary<int, (TimeSpan, TimeSpan)> threadWeights = null) {
    FunctionDebugInfo = funcInfo;
    FunctionId = functionId;
    ThreadWeights = threadWeights ?? new Dictionary<int, (TimeSpan, TimeSpan)>();
    children_ = new TinyList<ProfileCallTreeNode>(children);
    caller_ = caller;
    CallSites = callSites;
  }

  public void AccumulateWeight(TimeSpan weight) {
    Weight += weight;
  }

  public void AccumulateWeight(TimeSpan weight, TimeSpan exclusiveWeight, int threadId) {
    ref var currentValue = ref CollectionsMarshal.GetValueRefOrAddDefault(ThreadWeights, threadId, out bool exists);

    // The TimeSpan + operator does an overflow check that is not relevant
    // (and an exception undesirable), avoid it for some speedup.
    currentValue.Weight = TimeSpan.FromTicks(currentValue.Weight.Ticks + weight.Ticks);
    currentValue.ExclusiveWeight = TimeSpan.FromTicks(currentValue.ExclusiveWeight.Ticks + exclusiveWeight.Ticks);
  }

  public List<(int ThreadId, (TimeSpan Weight, TimeSpan ExclusiveWeight) Values)>
    SortedByWeightPerThreadWeights {
    get {
      var list = new List<(int ThreadId, (TimeSpan Weight, TimeSpan ExclusiveWeight) Values)>(ThreadWeights.Count);

      foreach (var pair in ThreadWeights) {
        list.Add((pair.Key, pair.Value));
      }

      list.Sort((a, b) => b.Values.Weight.CompareTo(a.Values.Weight));
      return list;
    }
  }

  public List<(int ThreadId, (TimeSpan Weight, TimeSpan ExclusiveWeight) Values)>
    SortedByIdPerThreadWeights {
    get {
      var list = new List<(int ThreadId, (TimeSpan Weight, TimeSpan ExclusiveWeight) Values)>(ThreadWeights.Count);

      foreach (var pair in ThreadWeights) {
        list.Add((pair.Key, pair.Value));
      }

      list.Sort((a, b) => a.ThreadId.CompareTo(b.ThreadId));
      return list;
    }
  }

  public void AccumulateExclusiveWeight(TimeSpan weight) {
    ExclusiveWeight += weight;
  }

  public (ProfileCallTreeNode, bool) AddChild(FunctionDebugInfo functionDebugInfo, ProfileFunctionId functionId) {
    return GetOrCreateChildNode(functionDebugInfo, functionId);
  }

  public bool HasChild(ProfileCallTreeNode node) {
    return children_.Contains(node);
  }

  public ProfileCallTreeNode FindChildNode(ProfileFunctionId functionId) {
    return children_.Find(node => node.FunctionId == functionId);
  }

  internal void SetChildrenNoLock(List<ProfileCallTreeNode> children) {
    // Used by ProfileCallTree.Deserialize.
    children_ = new TinyList<ProfileCallTreeNode>(children);
  }

  internal void SetParent(ProfileCallTreeNode parentNode) {
    // Used by ProfileCallTree.Deserialize.
    caller_ = parentNode;
  }

  public bool HasParent(ProfileCallTreeNode parentNode, ProfileCallTreeNodeComparer comparer) {
    return caller_ != null && comparer.Equals(caller_, parentNode);
  }

  private (ProfileCallTreeNode, bool)
    GetOrCreateChildNode(FunctionDebugInfo functionDebugInfo, ProfileFunctionId functionId) {
    var childNode = FindExistingNode(functionDebugInfo, functionId);

    if (childNode != null) {
      return (childNode, false);
    }

    childNode = new ProfileCallTreeNode(functionDebugInfo, functionId, null, this);
    children_.Add(childNode);
    return (childNode, true);
  }

  public void AddCallSite(ProfileCallTreeNode childNode, long rva, TimeSpan weight) {
    CallSites ??= new Dictionary<long, ProfileCallSite>();
    ref var callsite = ref CollectionsMarshal.GetValueRefOrAddDefault(CallSites, rva, out bool exists);

    if (!exists) {
      callsite = new ProfileCallSite(rva);
    }

    callsite.AddTarget(childNode, weight);
  }

  private ProfileCallTreeNode FindExistingNode(FunctionDebugInfo functionDebugInfo, ProfileFunctionId functionId) {
    for (int i = 0; i < children_.Count; i++) {
      var child = children_[i];

      if (child.FunctionId == functionId) {
        return child;
      }
    }

    return null;
  }

  public void MergeWith(ProfileCallTreeNode otherNode) {
    // Accumulate the weights and merge all data structures,
    // then recursively merge the common child nodes
    // and copy over any new child nodes.
    otherNode.Tag = MergedNodeTag; // Mark node as merged to be discarded later.
    Weight += otherNode.Weight;
    ExclusiveWeight += otherNode.ExclusiveWeight;

    if (otherNode.HasCallSites) {
      CallSites ??= new Dictionary<long, ProfileCallSite>();

      foreach (var callSite in otherNode.CallSites) {
        ref var existingCallSite =
          ref CollectionsMarshal.GetValueRefOrAddDefault(CallSites, callSite.Key, out bool exists);

        if (!exists) {
          existingCallSite = callSite.Value;
        }
        else {
          existingCallSite.MergeWith(callSite.Value);
        }
      }
    }

    if (otherNode.HasThreadWeights) {
      ThreadWeights ??= new Dictionary<int, (TimeSpan Weight, TimeSpan ExclusiveWeight)>();

      foreach (var threadWeight in otherNode.ThreadWeights) {
        AccumulateWeight(threadWeight.Value.Weight, threadWeight.Value.ExclusiveWeight, threadWeight.Key);
      }
    }

    if (otherNode.HasChildren) {
      foreach (var child in otherNode.children_) {
        var existingChild = FindChildNode(child.FunctionId);

        if (existingChild != null) {
          // Recursively merge child nodes.
          existingChild.MergeWith(child);
        }
        else {
          // Copy over the child from the other node.
          children_.Add(child);
        }
      }
    }
  }

  public bool IsMergeNode() {
    return Tag == MergedNodeTag;
  }

  public void ClearIsMergedNode() {
    Tag = null;
  }

  internal void Print(StringBuilder builder, int level = 0, bool caller = false) {
    builder.Append(new string(' ', level * 4));
    builder.AppendLine($"{FunctionDebugInfo.Name}, RVA {FunctionDebugInfo.RVA}, Id {Id}");
    builder.Append(new string(' ', level * 4));
    builder.AppendLine($"    weight {Weight.TotalMilliseconds}");
    builder.Append(new string(' ', level * 4));
    builder.AppendLine($"    exc weight {ExclusiveWeight.TotalMilliseconds}");
    builder.Append(new string(' ', level * 4));
    builder.AppendLine($"    callees: {(Children != null ? Children.Count : 0)}");

    if (Children != null && !caller) {
      foreach (var child in Children) {
        child.Print(builder, level + 1);
      }
    }
  }

  public bool Equals(ProfileCallTreeNode other) {
    if (ReferenceEquals(null, other)) {
      return false;
    }

    // Note that this holds only for nodes
    // belonging to the same ProfileCallTree instance.
    return Id == other.Id;
  }

  public override bool Equals(object obj) {
    if (ReferenceEquals(null, obj)) {
      return false;
    }

    if (ReferenceEquals(this, obj)) {
      return true;
    }

    if (obj.GetType() != GetType()) {
      return false;
    }

    return Equals((ProfileCallTreeNode)obj);
  }

  public override int GetHashCode() {
    return Id.GetHashCode();
  }

  public static bool operator ==(ProfileCallTreeNode left, ProfileCallTreeNode right) {
    return Equals(left, right);
  }

  public static bool operator !=(ProfileCallTreeNode left, ProfileCallTreeNode right) {
    return !Equals(left, right);
  }

  public override string ToString() {
    return $"Name: {FunctionDebugInfo?.Name}\n" +
           $"RVA {FunctionDebugInfo.RVA}, Id {Id}\n" +
           $"Weight: {Weight}\n" +
           $"ExclusiveWeight: {ExclusiveWeight}\n" +
           $"Children: {Children?.Count ?? 0}\n" +
           $"CallSites: {CallSites?.Count ?? 0}";
  }

  public ProfileCallTreeNode Clone() {
    return new ProfileCallTreeNode {
      Id = Id,
      Kind = Kind,
      FunctionId = FunctionId,
      FunctionDebugInfo = FunctionDebugInfo,
      Weight = Weight,
      ExclusiveWeight = ExclusiveWeight,
      children_ = children_,
      caller_ = caller_,
      CallSites = CallSites
    };
  }
}

public sealed class ProfileCallTreeGroupNode : ProfileCallTreeNode {
  private List<ProfileCallTreeNode> nodes_;
  private List<ProfileCallTreeNode> callers_;

  public ProfileCallTreeGroupNode() {
  }

  public ProfileCallTreeGroupNode(FunctionDebugInfo funcInfo, ProfileFunctionId functionId,
                                  List<ProfileCallTreeNode> nodes = null,
                                  List<ProfileCallTreeNode> children = null,
                                  List<ProfileCallTreeNode> callers = null,
                                  Dictionary<long, ProfileCallSite> callSites = null,
                                  Dictionary<int, (TimeSpan, TimeSpan)> threadWeights = null) :
    base(funcInfo, functionId, children, null, callSites, threadWeights) {
    nodes_ = nodes ?? new List<ProfileCallTreeNode>();
    callers_ = callers ?? new List<ProfileCallTreeNode>();
  }

  public ProfileCallTreeGroupNode(FunctionDebugInfo funcInfo, ProfileFunctionId functionId,
                                  ProfileCallTreeNodeKind kind) :
    base(funcInfo, functionId) {
    nodes_ = new List<ProfileCallTreeNode>();
    Kind = kind;
  }

  public ProfileCallTreeGroupNode(ProfileCallTreeNode baseNode, TimeSpan weight) :
    this(baseNode.FunctionDebugInfo, baseNode.FunctionId) {
    nodes_.Add(baseNode);
    Weight = weight;
  }

  public override bool IsGroup => true;
  public override List<ProfileCallTreeNode> Nodes => nodes_;
  public override List<ProfileCallTreeNode> Callers => callers_;
  public override bool HasCallers => callers_ != null && callers_.Count > 0;

  public override string ToString() {
    return $"{FunctionDebugInfo.Name}, RVA {FunctionDebugInfo.RVA}, Id {Id}, Nodes: {nodes_.Count}";
  }
}

// Comparer used for the root nodes in order to ignore the ID part.
public class ProfileCallTreeNodeComparer : IEqualityComparer<ProfileCallTreeNode> {
  public bool Equals(ProfileCallTreeNode x, ProfileCallTreeNode y) {
    return x.FunctionId == y.FunctionId;
  }

  public int GetHashCode(ProfileCallTreeNode obj) {
    return obj.FunctionId.GetHashCode();
  }
}