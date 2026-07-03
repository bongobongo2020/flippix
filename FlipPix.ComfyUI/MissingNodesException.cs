using System;
using System.Collections.Generic;
using System.Linq;
using FlipPix.Core.Models;

namespace FlipPix.ComfyUI;

/// <summary>
/// Thrown by pre-submit validation when a workflow references custom-node types the connected
/// ComfyUI doesn't have loaded. Surfaced instead of ComfyUI's raw "missing_node_type" BadRequest
/// dump so the UI can show a clear, actionable message (and offer to install the packs).
/// </summary>
public class MissingNodesException : Exception
{
    /// <summary>The distinct node class types the workflow needs but ComfyUI doesn't expose.</summary>
    public IReadOnlyList<string> MissingNodes { get; }

    /// <summary>The same missing nodes with their resolved providing pack, for the resolver.</summary>
    public IReadOnlyList<MissingNodeInfo> Details { get; }

    public MissingNodesException(string message, IReadOnlyList<MissingNodeInfo> details)
        : base(message)
    {
        Details = details;
        MissingNodes = details.Select(d => d.ClassType).ToList();
    }
}
