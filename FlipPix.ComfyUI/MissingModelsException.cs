using System;
using System.Collections.Generic;

namespace FlipPix.ComfyUI;

/// <summary>
/// Thrown by pre-submit validation when a workflow references model files that the connected
/// ComfyUI server does not have. Surfaced instead of ComfyUI's raw "value_not_in_list"
/// BadRequest dump so the UI can show a clear, actionable message.
/// </summary>
public class MissingModelsException : Exception
{
    /// <summary>The distinct model filenames the workflow needs but ComfyUI doesn't expose.</summary>
    public IReadOnlyList<string> MissingModels { get; }

    public MissingModelsException(string message, IReadOnlyList<string> missingModels)
        : base(message)
    {
        MissingModels = missingModels;
    }
}
