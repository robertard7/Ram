using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class ModelOutputValidationService
{
    private readonly ToolRequestParser _toolRequestParser = new();

    public ModelOutputValidationResult Validate(ResponseMode mode, string output)
    {
        return mode switch
        {
            ResponseMode.ToolRequired => ValidateToolLikeOutput(output, "non_tool_response_for_tool_required_mode"),
            ResponseMode.ChainRequired => ValidateToolLikeOutput(output, "non_chain_response_for_chain_required_mode"),
            ResponseMode.SummaryOnly => ValidateSummaryOnlyOutput(output),
            _ => new ModelOutputValidationResult
            {
                IsValid = true
            }
        };
    }

    private ModelOutputValidationResult ValidateToolLikeOutput(string output, string freeTextReason)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new ModelOutputValidationResult
            {
                IsValid = false,
                RejectionReason = freeTextReason
            };
        }

        var toolRequestMatches = Regex.Matches(output, @"TOOL_REQUEST", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (toolRequestMatches.Count > 1)
        {
            return new ModelOutputValidationResult
            {
                IsValid = false,
                RejectionReason = "multiple_unsolicited_actions"
            };
        }

        var parsedRequest = _toolRequestParser.Parse(output);
        if (parsedRequest is not null)
        {
            return new ModelOutputValidationResult
            {
                IsValid = true,
                ParsedToolRequest = parsedRequest
            };
        }

        if (_toolRequestParser.LooksLikeToolRequest(output) || LooksLikeFakeToolNarration(output))
        {
            return new ModelOutputValidationResult
            {
                IsValid = false,
                RejectionReason = "free_text_not_allowed"
            };
        }

        return new ModelOutputValidationResult
        {
            IsValid = false,
            RejectionReason = freeTextReason
        };
    }

    private ModelOutputValidationResult ValidateSummaryOnlyOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new ModelOutputValidationResult
            {
                IsValid = false,
                RejectionReason = "free_text_not_allowed"
            };
        }

        return _toolRequestParser.LooksLikeToolRequest(output) || LooksLikeFakeToolNarration(output)
            ? new ModelOutputValidationResult
            {
                IsValid = false,
                RejectionReason = "tool_protocol_garbage_in_summary_mode"
            }
            : new ModelOutputValidationResult
            {
                IsValid = true
            };
    }

    private static bool LooksLikeFakeToolNarration(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains("Tool request:", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(output, @"^\s*name\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)
            || Regex.IsMatch(output, @"^\s*reason\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }
}
