using System;
using System.Collections.Generic;
using System.Text.Json;
using FlipPix.Core.Interfaces;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Utility for updating ComfyUI workflow JSON node inputs
    /// </summary>
    public static class WorkflowNodeUpdater
    {
        /// <summary>
        /// Update a single input value in a workflow node
        /// </summary>
        /// <param name="workflowJson">The workflow JSON as a string</param>
        /// <param name="nodeId">The ID of the node to update</param>
        /// <param name="inputName">The name of the input to update</param>
        /// <param name="value">The new value</param>
        /// <returns>Updated workflow JSON as JsonElement</returns>
        public static JsonElement UpdateNodeInput(ref string workflowJson, string nodeId, string inputName, object value)
        {
            if (string.IsNullOrWhiteSpace(workflowJson))
                throw new ArgumentException("Workflow JSON cannot be null or empty", nameof(workflowJson));
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Node ID cannot be null or empty", nameof(nodeId));
            if (string.IsNullOrWhiteSpace(inputName))
                throw new ArgumentException("Input name cannot be null or empty", nameof(inputName));

            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
            if (workflowDict == null)
                throw new InvalidOperationException("Failed to deserialize workflow JSON");

            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs[inputName] = value;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            var updatedJson = JsonSerializer.Serialize(workflowDict);
            workflowJson = updatedJson;
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        /// <summary>
        /// Update multiple input values in a workflow node at once
        /// </summary>
        /// <param name="workflowJson">The workflow JSON as a string</param>
        /// <param name="nodeId">The ID of the node to update</param>
        /// <param name="inputs">Dictionary of input names and their new values</param>
        /// <returns>Updated workflow JSON as JsonElement</returns>
        public static JsonElement UpdateNodeInputMultiple(ref string workflowJson, string nodeId, Dictionary<string, object> inputs)
        {
            if (string.IsNullOrWhiteSpace(workflowJson))
                throw new ArgumentException("Workflow JSON cannot be null or empty", nameof(workflowJson));
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Node ID cannot be null or empty", nameof(nodeId));
            if (inputs == null || inputs.Count == 0)
                throw new ArgumentException("Inputs dictionary cannot be null or empty", nameof(inputs));

            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
            if (workflowDict == null)
                throw new InvalidOperationException("Failed to deserialize workflow JSON");

            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var nodeInputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (nodeInputs != null)
                    {
                        foreach (var kvp in inputs)
                        {
                            nodeInputs[kvp.Key] = kvp.Value;
                        }
                        node["inputs"] = nodeInputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            var updatedJson = JsonSerializer.Serialize(workflowDict);
            workflowJson = updatedJson;
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        /// <summary>
        /// Get the current value of an input from a workflow node
        /// </summary>
        /// <param name="workflowJson">The workflow JSON as a string</param>
        /// <param name="nodeId">The ID of the node</param>
        /// <param name="inputName">The name of the input</param>
        /// <returns>The input value, or null if not found</returns>
        public static object? GetNodeInput(string workflowJson, string nodeId, string inputName)
        {
            if (string.IsNullOrWhiteSpace(workflowJson) ||
                string.IsNullOrWhiteSpace(nodeId) ||
                string.IsNullOrWhiteSpace(inputName))
            {
                return null;
            }

            try
            {
                var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflowDict == null || !workflowDict.ContainsKey(nodeId))
                {
                    return null;
                }

                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node == null || !node.ContainsKey("inputs"))
                {
                    return null;
                }

                var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(node["inputs"]));

                return inputs != null && inputs.ContainsKey(inputName) ? inputs[inputName] : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
