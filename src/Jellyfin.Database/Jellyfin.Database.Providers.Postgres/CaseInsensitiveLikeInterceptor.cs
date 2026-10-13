using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Gives <see cref="DbFunctionsExtensions.Like(DbFunctions, string, string)"/> the meaning it has on SQLite.
/// </summary>
/// <remarks>
/// SQLite's LIKE ignores the case of ASCII letters and has no escape character unless one is given, while PostgreSQL's
/// LIKE is case sensitive and treats a backslash as escape character. Searches written against SQLite, e.g. for activity
/// log entries or titles, would stop matching on PostgreSQL. ILIKE with an explicitly empty escape behaves like SQLite.
/// </remarks>
internal sealed class CaseInsensitiveLikeInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo _like = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string)])!;

    private static readonly MethodInfo _likeWithEscape = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    private static readonly MethodInfo _iLikeWithEscape = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
        nameof(NpgsqlDbFunctionsExtensions.ILike),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new LikeRewriter().Visit(queryExpression);

    private sealed class LikeRewriter : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == _like)
            {
                return Expression.Call(
                    _iLikeWithEscape,
                    Visit(node.Arguments[0]),
                    Visit(node.Arguments[1]),
                    Visit(node.Arguments[2]),
                    Expression.Constant(string.Empty));
            }

            if (node.Method == _likeWithEscape)
            {
                return Expression.Call(
                    _iLikeWithEscape,
                    Visit(node.Arguments[0]),
                    Visit(node.Arguments[1]),
                    Visit(node.Arguments[2]),
                    Visit(node.Arguments[3]));
            }

            return base.VisitMethodCall(node);
        }
    }
}
