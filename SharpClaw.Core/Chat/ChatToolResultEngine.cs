using System.Text;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral chat tool-result shaping rules used by SharpClaw runtimes.
/// </summary>
public sealed class ChatToolResultEngine
{
    /// <summary>
    /// Marker separating tool-result text from screenshot image payload data.
    /// </summary>
    public const string ScreenshotMarker = "[SCREENSHOT_BASE64]";

    /// <summary>
    /// Combines persisted tool notation and the final provider text.
    /// </summary>
    public string BuildFinalAssistantContent(
        string toolNotation,
        string? modelContent)
    {
        return toolNotation.Length > 0
            ? toolNotation + "\n" + (modelContent ?? "")
            : modelContent ?? "";
    }

    /// <summary>
    /// Builds user-visible assistant text for malformed local-inference tool-loop envelopes.
    /// </summary>
    public string BuildMalformedEnvelopeAssistantContent(
        string toolNotation,
        string? payloadPreview)
    {
        var message = new StringBuilder();
        if (toolNotation.Length > 0)
            message.Append(toolNotation).Append("\n");

        message.Append(
            "Error: the local model returned malformed tool-loop output after a tool call. " +
            "The model likely lost the required JSON envelope format for the follow-up response. ");

        if (!string.IsNullOrWhiteSpace(payloadPreview))
        {
            message.Append("Preview: ");
            message.Append(payloadPreview.Trim());
        }

        return message.ToString();
    }

    /// <summary>
    /// Splits tool-result text from a base64 screenshot payload when present.
    /// </summary>
    public (string? TextResult, string? ImageBase64) ExtractScreenshotData(
        string? resultData)
    {
        if (resultData is null)
            return (null, null);

        var markerIndex = resultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return (resultData, null);

        var textPart = resultData[..markerIndex].TrimEnd();
        var base64Part = resultData[(markerIndex + ScreenshotMarker.Length)..];
        return (textPart, base64Part);
    }

    /// <summary>
    /// Builds the provider-facing tool-result message for a completed job.
    /// </summary>
    public ToolAwareMessage BuildToolResultMessage(
        string toolCallId,
        AgentJobResponse job,
        bool supportsVision)
    {
        ArgumentNullException.ThrowIfNull(job);

        var (textResult, imageBase64) = ExtractScreenshotData(job.ResultData);

        var resultContent =
            $"status={job.Status}" +
            (textResult is not null ? $" result={textResult}" : "") +
            (job.ErrorMessage is not null ? $" error={job.ErrorMessage}" : "");

        if (imageBase64 is not null && supportsVision)
        {
            return ToolAwareMessage.ToolResultWithImage(
                toolCallId,
                resultContent,
                imageBase64,
                "image/png");
        }

        if (imageBase64 is not null)
            resultContent += " (screenshot captured successfully)";

        return ToolAwareMessage.ToolResult(toolCallId, resultContent);
    }

    /// <summary>
    /// Applies one provider round's token usage to already-returned job
    /// response snapshots. Any remainder is assigned to the first round job.
    /// </summary>
    public void ApplyRoundTokenUsageToJobResponses(
        IList<AgentJobResponse> jobResults,
        IReadOnlyList<Guid> roundJobIds,
        int promptTokens,
        int completionTokens)
    {
        ArgumentNullException.ThrowIfNull(jobResults);
        ArgumentNullException.ThrowIfNull(roundJobIds);

        if (promptTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(promptTokens),
                promptTokens,
                "Prompt tokens cannot be negative.");
        if (completionTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(completionTokens),
                completionTokens,
                "Completion tokens cannot be negative.");
        if (roundJobIds.Count == 0)
            return;

        var count = roundJobIds.Count;
        var promptPer = promptTokens / count;
        var completionPer = completionTokens / count;
        var promptRemainder = promptTokens % count;
        var completionRemainder = completionTokens % count;

        for (var roundIndex = 0; roundIndex < roundJobIds.Count; roundIndex++)
        {
            var id = roundJobIds[roundIndex];
            var promptShare = promptPer
                + (roundIndex == 0 ? promptRemainder : 0);
            var completionShare = completionPer
                + (roundIndex == 0 ? completionRemainder : 0);

            for (var jobIndex = jobResults.Count - 1; jobIndex >= 0; jobIndex--)
            {
                if (jobResults[jobIndex].Id != id)
                    continue;

                var existing = jobResults[jobIndex].JobCost;
                var newPrompt = (existing?.TotalPromptTokens ?? 0) + promptShare;
                var newCompletion =
                    (existing?.TotalCompletionTokens ?? 0) + completionShare;

                jobResults[jobIndex] = jobResults[jobIndex] with
                {
                    JobCost = new TokenUsageResponse(
                        newPrompt,
                        newCompletion,
                        newPrompt + newCompletion)
                };
                break;
            }
        }
    }
}
