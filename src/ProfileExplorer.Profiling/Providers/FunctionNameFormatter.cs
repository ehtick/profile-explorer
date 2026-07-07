// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace ProfileExplorer.Core.Providers;

/// <summary>
/// Formats a raw (possibly mangled) function name into a display name.
/// </summary>
public delegate string FunctionNameFormatter(string name);
