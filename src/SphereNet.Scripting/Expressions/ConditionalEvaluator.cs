namespace SphereNet.Scripting.Expressions;

/// <summary>
/// Evaluates conditional expressions for IF/ELIF/ELSE script flow.
/// Maps to CExpression::EvaluateConditional* in Source-X.
/// </summary>
public sealed class ConditionalEvaluator
{
    private readonly ExpressionParser _expr;

    public ConditionalEvaluator(ExpressionParser expr)
    {
        _expr = expr;
    }

    /// <summary>
    /// Evaluate a full conditional expression that may contain multiple sub-conditions.
    /// Returns true/false.
    /// </summary>
    public bool Evaluate(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return false;

        string resolved = _expr.EvaluateStr(condition);
        long result = _expr.Evaluate(resolved.AsSpan());
        return result != 0;
    }
}
