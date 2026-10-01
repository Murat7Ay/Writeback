using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Writeback.Mapping;

/// <summary>
/// Reads property names from selector lambdas: <c>x =&gt; x.Name</c> or <c>x =&gt; new { x.Name, x.Email }</c>.
/// Only direct properties of the lambda parameter are accepted, so selectors can never smuggle in computed SQL.
/// </summary>
internal static class MemberSelector
{
    public static IReadOnlyList<string> GetPropertyNames(LambdaExpression selector, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(selector, parameterName);
        var parameter = selector.Parameters[0];
        var body = StripConvert(selector.Body);

        if (body is NewExpression newExpression)
        {
            var names = new List<string>(newExpression.Arguments.Count);
            foreach (var argument in newExpression.Arguments)
            {
                names.Add(GetDirectProperty(StripConvert(argument), parameter, selector, parameterName));
            }

            if (names.Count == 0)
            {
                throw new ArgumentException($"Selector '{selector}' selects no properties.", parameterName);
            }

            return names;
        }

        return new[] { GetDirectProperty(body, parameter, selector, parameterName) };
    }

    private static string GetDirectProperty(Expression expression, ParameterExpression parameter, LambdaExpression selector, string parameterName)
    {
        if (expression is MemberExpression { Member: PropertyInfo property } member && member.Expression == parameter)
        {
            return property.Name;
        }

        throw new ArgumentException(
            $"Selector '{selector}' must select properties of '{parameter.Name}' directly, e.g. x => x.Name or x => new {{ x.Name, x.Email }}.",
            parameterName);
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }
}
