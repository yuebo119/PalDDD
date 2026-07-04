using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 📐 ISpecification<T> — DDD 规约模式（轻量级）
// ─────────────────────────────────────────────────────────────
//
// 💡 通俗解释 —— 什么是规约？
//   ｜ 规约（Specification）是一个「判断标准」，用来回答「这个对象满足条件吗？」
//   ｜ 例如：「订单金额大于 100」「用户已激活」「商品有库存」都是规约。
//   ｜ 规约可以组合：「金额大于 100 且 用户已激活」→ And 操作。
//
// 💡 为什么放在 PalDDD.Core？
//   ｜ 规约表达的是领域规则 —— 属于领域层的战术 DDD 模式。
//   ｜ Specification 不依赖任何 ORM 或基础设施，可以安全地放在 Core。
//   ｜ 与 IPalValidator 同级：Validator 产生验证错误列表，Specification 产生布尔判断。
//
// 💡 两种使用路径：
//   ｜ 1. 内存判断：IsSatisfiedBy(entity) —— 领域层使用，纯内存操作
//   ｜ 2. EF Core 查询：ToExpression() —— 转为 Where() 的 lambda，数据库执行
//   ｜ Dapper 不强制集成：应用层自行将规约转为 SQL WHERE 子句
// ─────────────────────────────────────────────────────────────

/// <summary>
/// DDD 规约接口 — 封装可复用的领域规则。
/// <para>
/// 规约是可组合的：通过 <c>And</c> / <c>Or</c> / <c>Not</c> 构建复杂条件，
/// 避免在领域逻辑中内联大量 if 判断。
/// </para>
/// </summary>
/// <typeparam name="T">规约适用的实体/值对象类型</typeparam>
/// <remarks>
/// <b>与 IPalValidator 的区别：</b><br/>
/// — IPalValidator 返回 <see cref="PalValidationResult"/>（含多个错误描述），用于输入验证<br/>
/// — ISpecification 返回 <c>bool</c>，用于业务规则判断和查询过滤
/// </remarks>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "And/Or/Not 是 DDD Specification 模式的标准命名，VB.NET 兼容性非本项目关注。")]
public interface ISpecification<T>
{
    /// <summary>在内存中判断实体是否满足此规约</summary>
    bool IsSatisfiedBy(T entity);

    /// <summary>
    /// 将规约转为表达式树 — 用于 EF Core 的 <c>Where()</c> 查询过滤。
    /// <para>
    /// 💡 <b>组合规约的 EF Core 兼容性：</b>使用 ParameterReplacer 将子表达式参数统一替换，
    /// 生成的表达式树可被 EF Core 的 LINQ 提供器完整翻译为 SQL。
    /// 单层规约和组合规约（And/Or/Not）均完全兼容 EF Core 8+。
    /// </para>
    /// <para>Dapper 使用者可调用此方法获取表达式，自行转为 SQL 或直接在内存中过滤。</para>
    /// </summary>
    Expression<Func<T, bool>> ToExpression();

    /// <summary>逻辑与 — 两个规约同时满足</summary>
    ISpecification<T> And(ISpecification<T> other)
        => new AndSpecification<T>(this, other);

    /// <summary>逻辑或 — 任一规约满足</summary>
    ISpecification<T> Or(ISpecification<T> other)
        => new OrSpecification<T>(this, other);

    /// <summary>逻辑非 — 规约不满足</summary>
    ISpecification<T> Not()
        => new NotSpecification<T>(this);
}

// ─────────────────────────────────────────────────────────────
// DIM 组合实现 — 内部类，零外部依赖
// ─────────────────────────────────────────────────────────────
//
// 💡 Expression 组合策略：
//   使用 ParameterReplacer 将左右表达式的参数替换为公共参数，
//   而非 Expression.Invoke。后者生成的表达式树在 EF Core 等 LINQ
//   提供器中无法翻译为 SQL，会导致运行时 NotSupportedException。
// ─────────────────────────────────────────────────────────────

/// <summary>表达式参数替换器 — 将表达式树中的参数引用统一替换为目标参数</summary>
internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _oldParam;
    private readonly ParameterExpression _newParam;

    public ParameterReplacer(ParameterExpression oldParam, ParameterExpression newParam)
    {
        _oldParam = oldParam;
        _newParam = newParam;
    }

    protected override Expression VisitParameter(ParameterExpression node)
        => node == _oldParam ? _newParam : base.VisitParameter(node);
}

internal sealed class AndSpecification<T> : ISpecification<T>
{
    private readonly ISpecification<T> _left;
    private readonly ISpecification<T> _right;

    public AndSpecification(ISpecification<T> left, ISpecification<T> right)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
    }

    public bool IsSatisfiedBy(T entity)
        => _left.IsSatisfiedBy(entity) && _right.IsSatisfiedBy(entity);

    public Expression<Func<T, bool>> ToExpression()
    {
        var leftExpr = _left.ToExpression();
        var rightExpr = _right.ToExpression();
        var parameter = Expression.Parameter(typeof(T));

        var leftBody = new ParameterReplacer(leftExpr.Parameters[0], parameter).Visit(leftExpr.Body);
        var rightBody = new ParameterReplacer(rightExpr.Parameters[0], parameter).Visit(rightExpr.Body);

        return Expression.Lambda<Func<T, bool>>(
            Expression.AndAlso(leftBody, rightBody), parameter);
    }
}

internal sealed class OrSpecification<T> : ISpecification<T>
{
    private readonly ISpecification<T> _left;
    private readonly ISpecification<T> _right;

    public OrSpecification(ISpecification<T> left, ISpecification<T> right)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
    }

    public bool IsSatisfiedBy(T entity)
        => _left.IsSatisfiedBy(entity) || _right.IsSatisfiedBy(entity);

    public Expression<Func<T, bool>> ToExpression()
    {
        var leftExpr = _left.ToExpression();
        var rightExpr = _right.ToExpression();
        var parameter = Expression.Parameter(typeof(T));

        var leftBody = new ParameterReplacer(leftExpr.Parameters[0], parameter).Visit(leftExpr.Body);
        var rightBody = new ParameterReplacer(rightExpr.Parameters[0], parameter).Visit(rightExpr.Body);

        return Expression.Lambda<Func<T, bool>>(
            Expression.OrElse(leftBody, rightBody), parameter);
    }
}

internal sealed class NotSpecification<T> : ISpecification<T>
{
    private readonly ISpecification<T> _inner;

    public NotSpecification(ISpecification<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public bool IsSatisfiedBy(T entity)
        => !_inner.IsSatisfiedBy(entity);

    public Expression<Func<T, bool>> ToExpression()
    {
        var innerExpr = _inner.ToExpression();
        var parameter = Expression.Parameter(typeof(T));

        var innerBody = new ParameterReplacer(innerExpr.Parameters[0], parameter).Visit(innerExpr.Body);

        return Expression.Lambda<Func<T, bool>>(
            Expression.Not(innerBody), parameter);
    }
}

/// <summary>
/// 规约构建器 — 简化组合规约的创建。
/// <para>
/// 通过静态工厂方法 <c>Spec&lt;T&gt;.Where(...)</c> 创建规约，
/// 然后链式组合。
/// </para>
/// </summary>
/// <example>
/// <code>
/// var spec = Spec&lt;Order&gt;.Where(o => o.Amount > 100)
///     .And(Spec&lt;Order&gt;.Where(o => o.Status == OrderStatus.Active));
///
/// // 内存判断（组合规约的 IsSatisfiedBy 使用短路逻辑）
/// if (spec.IsSatisfiedBy(order)) { ... }
///
/// // EF Core 查询（组合规约使用 ParameterReplacer，EF Core 8+ 可翻译为 SQL）
/// var activeOrders = dbContext.Orders.Where(spec.ToExpression()).ToList();
/// </code>
/// </example>
public static class Spec<T>
{
    /// <summary>
    /// 创建一个基于 lambda 表达式的规约。
    /// </summary>
    /// <param name="predicate">判断条件（同时也是 EF Core 的查询表达式）</param>
    public static ISpecification<T> Where(Expression<Func<T, bool>> predicate)
        => new ExpressionSpecification<T>(predicate);

    /// <summary>始终满足的规约（恒真）</summary>
    public static ISpecification<T> All => new ExpressionSpecification<T>(_ => true);

    /// <summary>始终不满足的规约（恒假）</summary>
    public static ISpecification<T> None => new ExpressionSpecification<T>(_ => false);
}

internal sealed class ExpressionSpecification<T> : ISpecification<T>
{
    private readonly Expression<Func<T, bool>> _expression;
    private readonly Func<T, bool> _compiled;

    public ExpressionSpecification(Expression<Func<T, bool>> expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
        _compiled = _expression.Compile();
    }

    public bool IsSatisfiedBy(T entity) => _compiled(entity);

    public Expression<Func<T, bool>> ToExpression() => _expression;
}
