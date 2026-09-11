namespace FlowOps.Domain;

/// <summary>
/// Thrown when an operation would violate a domain rule. <see cref="RuleCode"/> carries the
/// rule ID from docs/domain-model.md so callers (and logs) can trace a rejection back to the
/// exact rule that produced it, per CLAUDE.md §11.3.
/// </summary>
public sealed class DomainRuleException : Exception
{
    public string RuleCode { get; }

    public DomainRuleException(string ruleCode, string message)
        : base(message)
    {
        RuleCode = ruleCode;
    }
}
