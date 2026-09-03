using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.SourceGen;


// ─────────────────────────────────────────────────────────────
// 源码生成器 — 智能枚举
// ─────────────────────────────────────────────────────────────

/// <summary>智能枚举增量源码生成器 — 编译时 walk 语法树，产出硬编码字段引用，完全 AOT 兼容</summary>
/// <remarks>
/// 生成代码通过调用 <c>RegisterValues([Field1, Field2, ...])</c> 在静态构造函数中注入编译时已知的值。<br/>
/// 运行时零反射——所有字段名在编译时已确定。<br/>
/// 用法：<c>[GenerateEnum] public partial class OrderStatus : SmartEnum&lt;OrderStatus, string&gt; { ... }</c>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EnumGenerator : IIncrementalGenerator
{
    private const string AttrName = "PalDDD.Core.GenerateEnumAttribute";

    private static readonly DiagnosticDescriptor NoFieldsWarning = new(
        "PALENUM001",
        "Generated enum has no static fields to register",
        // P3 修复（十七轮）：文案对齐实际收集逻辑——字段收集只要求 public/internal static
        // （不要求 readonly），原文案 "static readonly" 与实现不符
        // ITM-100 修复：文案补 internal——收集条件为 static && (public || internal)，
        // 原文案 "public static" 与实现不符（仅 internal static 字段的枚举会误报 PALENUM001）
        "Type '{0}' is marked with [GenerateEnum] but has no public or internal static fields. Add at least one field or remove the attribute.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotDirectInheritanceError = new(
        "PALENUM002",
        "GenerateEnum requires direct SmartEnum inheritance",
        "Type '{0}' is marked with [GenerateEnum] but does not directly inherit SmartEnum<TSelf, TValue> (found '{1}'). GenerateEnum only supports direct inheritance.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P3 修复（八轮评审）：record 声明此前被 predicate 静默跳过（attribute 挂着但零生成）——
    // 仿 PALENUM002 模式报 PALENUM003 引导改用 class
    private static readonly DiagnosticDescriptor RecordNotSupportedError = new(
        "PALENUM003",
        "GenerateEnum does not support record declarations",
        "Type '{0}' is a record declaration marked with [GenerateEnum]. GenerateEnum only supports partial class declarations; change 'record' to 'class'.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）生成坏代码——
    // 生成物以裸名声明 partial class（与用户泛型声明同名冲突），且 [ModuleInitializer]
    // 不允许位于泛型类型成员。编译期报 PALENUM004 引导移出泛型声明，不生成坏代码。
    private static readonly DiagnosticDescriptor GenericDeclarationNotSupported = new(
        "PALENUM004",
        "GenerateEnum does not support generic declarations",
        "Type '{0}' is marked with [GenerateEnum] within a generic declaration. Generic smart enums are not supported; move the target out of the generic type or remove its type parameters.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P2 修复（二十一轮）：镜像 IdentityGenerator PALID004——同一类型的多个 partial
    // 声明均挂 [GenerateEnum] 时两个 candidate 的 hint 相同，AddSource 同 hint 第二次
    // 调用抛 ArgumentException 使整个生成器崩溃。重复声明报 PALENUM005，仅首个声明生成代码。
    private static readonly DiagnosticDescriptor DuplicatePartialDeclaration = new(
        "PALENUM005",
        "GenerateEnum attribute declared on multiple partial declarations",
        "Type '{0}' has [GenerateEnum] applied to multiple partial declarations. Apply the attribute to only one partial declaration.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v26 P3 生成器族：非 partial class 声明此前被 predicate 的 PartialKeyword 前置过滤
    // 静默跳过（attribute 挂着但零诊断零生成）——镜像 IdentityGenerator PALID002 的
    // "predicate 放宽 + transform 报诊断"模式报 PALENUM006，引导补 partial 修饰符
    //（生成物恒为 partial class，非 partial 用户声明无法与之合并，CS0260）
    private static readonly DiagnosticDescriptor NonPartialDeclarationError = new(
        "PALENUM006",
        "GenerateEnum target must be a partial class declaration",
        "Type '{0}' is marked with [GenerateEnum] but is not declared as a partial class. Add the 'partial' modifier so the generator can merge generated members.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v33 P3：private/protected nested 类型挂 [GenerateEnum] 时，生成物（namespace 级
    // partial class + [ModuleInitializer] 静态构造）以裸名引用该类型——可访问性低于
    // internal 的嵌套类型对生成物不可见（CS0122 落在 auto-generated 文件，排障困难）。
    // 编译期报 PALENUM007 不生成坏代码（顶层类型恒 public/internal 不受影响）
    // v35 P3（DA1）：v34 链检查升格后原消息 "is declared '{1}'" 仍描述目标自身——链中间层
    // 阻断场景（public Outer → private Mid → internal Inner）目标自身 internal，消息失实且
    // "raise the declaration" 指引无效（改目标自身救不了 Mid）。{1} 语义勘正为实际阻断层
    // 修饰符文本（ValueType 携带机制不变），措辞改 "declaration or its containing type chain
    // blocks visibility" 形态 + 指引补 "and its containing types"
    private static readonly DiagnosticDescriptor NonAccessibleDeclarationError = new(
        "PALENUM007",
        "GenerateEnum does not support inaccessible declarations",
        "Type '{0}' is marked with [GenerateEnum] but its declaration or its containing type chain blocks visibility (blocking declaration is '{1}'). Generated registration code cannot reference declarations below internal; raise the declaration (and its containing types) to internal or public.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v35 P3（DA4）：普通 struct / interface 挂 [GenerateEnum]——struct 的 BaseType 恒为
    // ValueType（interface 恒 null），无法继承 SmartEnum 基类，原落基类检查报 PALENUM002
    // "does not directly inherit SmartEnum"（误导用户"改基类"——struct/interface 根本没有
    // 可改的基类；且 GenerateEnumAttribute 的 AttributeTargets 限 Class，用户挂载点本就
    // 违例 CS0592）。镜像 record struct 的 PALENUM003 前置处理，报 PALENUM008 引导改用
    // partial class
    private static readonly DiagnosticDescriptor StructOrInterfaceNotSupportedError = new(
        "PALENUM008",
        "GenerateEnum target must be a class deriving SmartEnum",
        // v36 P3：消息去冠词语病——原文案 "but is a {1}" 在 interface 腿输出 "is a interface"
        //（冠词失配）；改为直陈两种声明种类，不再按 {1} 插值（{1} 仍由分派侧统一两参传入，
        // string.Format 忽略多余参数，消息不再引用）
        "Type '{0}' is marked with [GenerateEnum] but is a struct or interface declaration. Structs and interfaces cannot inherit the SmartEnum<TSelf, TValue> base class; change the declaration to a partial class deriving SmartEnum<TSelf, TValue>.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v53 P2（D 片）：非 partial 包含类型专用诊断——v52 引入检查时复用 PALENUM007，
    // {1} 被置人为字符串 "non-partial containing type"，且"raise to internal or public"
    // 指引对 non-partial 根因无效（正确动作是加 partial 修饰符，与可见性无关）。
    // {1} = 实际非 partial 包含类型名（ValueType 携带）
    private static readonly DiagnosticDescriptor ContainingTypeNotPartialError = new(
        "PALENUM009",
        "GenerateEnum requires partial containing types",
        "Type '{0}' is marked with [GenerateEnum] but its containing type '{1}' is not partial — the generated partial declaration cannot merge with it (CS0260 would land in auto-generated files). Add 'partial' to the containing declaration.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 步骤 1：收集所有标记了 [GenerateEnum] 的 class 声明及其静态字段
        // P3 修复（八轮评审）：predicate 同时匹配 record 声明（Class target 含 record class），
        // 由 transform 报 PALENUM003——此前静默跳过，用户无反馈
        // v26 P3 生成器族：predicate 放宽到全部类型声明——非 partial class 挂 [GenerateEnum]
        // 此前被 PartialKeyword 前置过滤静默跳过（零诊断零生成），改由 transform 报 PALENUM006
        //（镜像 IdentityGenerator PALID002 的 predicate 放宽模式）
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttrName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (context, ct) =>
            {
                var classSymbol = (INamedTypeSymbol)context.TargetSymbol;

                // ITM-100 修复：record struct 声明前置分支——原代码 IsRecord && TypeKind==Class
                // 不匹配 record struct（TypeKind==Struct），struct 无基类可继承 SmartEnum，
                // 落入下方基类检查报 PALENUM002（误导用户改基类）；补此分支与 record class
                // 一样报 PALENUM003（引导改用 class）
                if (classSymbol.IsRecord && classSymbol.TypeKind == TypeKind.Struct)
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: classSymbol.BaseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM003",
                        Location: context.TargetNode.GetLocation());
                }

                // P3 修复（八轮评审）：record 声明（GenerateEnumAttribute 的 Class target
                // 覆盖 record class）不支持——SmartEnum 的静态字段注册依赖 class 语义
                // P3 修复（十七轮）：DiagnosticMessage 死字段已删——诊断消息由 descriptor
                // 统一承载（RegisterSourceOutput 按 DiagnosticId 分派），payload 不再携带
                if (classSymbol.IsRecord && classSymbol.TypeKind == TypeKind.Class)
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: classSymbol.BaseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM003",
                        Location: context.TargetNode.GetLocation());
                }

                // v35 P3（DA4）：普通 struct / interface 分支——struct 的 BaseType 恒为
                // ValueType（interface 恒 null），无法继承 SmartEnum 基类，原落基类检查报
                // PALENUM002（误导）；前置报 PALENUM008 引导改用 partial class（镜像上方
                // record struct 的 PALENUM003 前置处理；record struct 已被上方分支捕获，
                // 不受本分支影响）。{1} 经 ValueType 携带声明种类（struct/interface），
                // 分派侧两参格式统一
                if (classSymbol.TypeKind is TypeKind.Struct or TypeKind.Interface)
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: classSymbol.TypeKind == TypeKind.Struct ? "struct" : "interface",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM008",
                        Location: context.TargetNode.GetLocation());
                }

                // v26 P3 生成器族：非 partial 声明报 PALENUM006——partial 可拆多文件，
                // 沿 DeclaringSyntaxReferences 检查任一声明带 partial（镜像 IdentityGenerator
                // 的 isPartialRecordStruct 形态——attribute 所在声明可能恰好非 partial，
                // 但同类型另一 partial 声明存在时生成物仍可合并）；全部声明非 partial 时
                // 生成物与用户类型无法合并（CS0260），不生成代码
                // v28 P3：GetSyntax 补传 ct（对齐同文件下方 partialDecl 收集路径的
                // reference.GetSyntax(ct) 与 MessageRegistryGenerator 形态）——增量管线
                // 取消信号可传播到语法物化
                var isPartial = classSymbol.DeclaringSyntaxReferences.Any(r =>
                    r.GetSyntax(ct) is TypeDeclarationSyntax d
                    && d.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword)));
                if (!isPartial)
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: classSymbol.BaseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM006",
                        Location: context.TargetNode.GetLocation());
                }

                // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）暂不支持
                // （见 GenericDeclarationNotSupported 注释）——编译期报 PALENUM004
                if (classSymbol.Arity > 0 || IsWithinGenericContainingType(classSymbol))
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: classSymbol.BaseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM004",
                        Location: context.TargetNode.GetLocation());
                }

                // v33 P3：可访问性拦截——可访问性低于 internal（private/protected 等）的
                // nested 类型对生成物不可见，RegisterValues 生成物引用必 CS0122。编译期报
                // PALENUM007（'{1}' 经 ValueType 携带阻断层修饰符文本，分派侧两参格式统一）
                // 不生成坏代码。v34 P2：检查升格为 ContainingType 全链（GeneratorAccessibility
                // 共享 helper）——中间层 private（public Outer → private Mid → internal Inner）
                // 同样阻断生成物可见性，仅查自身声明会漏
                if (GeneratorAccessibility.GetBlockingAccessibility(classSymbol) is { } blockingAccessibility)
                {
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: GeneratorAccessibility.AccessibilityToModifierText(blockingAccessibility),
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM007",
                        Location: context.TargetNode.GetLocation());
                }

                // 从基类 SmartEnum<TSelf, TValue> 提取 TValue
                var baseType = classSymbol.BaseType;
                // v26 P3 生成器族：SmartEnum 基类比对符号化——原 ToDisplayString() 字符串比对
                // 在 extern alias 下可能带别名前缀（"alias::PalDDD.Core.SmartEnum<TSelf, TValue>"）
                // 使精确匹配失配误报 PALENUM002（Error 级）。镜像 v25 IdentityGenerator 白名单
                // 的符号语义化（IsSystemGuid 形态）：Name + 命名空间链逐级比对 + Arity，
                // Symbol.Name 不含别名前缀，不受 extern alias 影响
                if (baseType is not INamedTypeSymbol { TypeArguments.Length: 2 } namedBase
                    || !IsPalSmartEnumBase(namedBase.OriginalDefinition))
                {
                    // P2 修复：隔层继承不再静默跳过——报 PALENUM002（与 PALENUM001 对称）
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: baseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM002",
                        Location: context.TargetNode.GetLocation());
                }

                var valueType = namedBase.TypeArguments[1];

                // P2 修复（嵌套类型）：镜像 IdentityGenerator——ContainingNamespace 不含
                // 类型层级，生成物需按 ContainingType 链包 partial 声明，否则 namespace 级
                // 平铺的同名类型与用户声明的嵌套 partial 不合并（平行类型）。
                var containingDeclarations = new List<string>();
                var containingNames = new List<string>();
                for (var t = classSymbol.ContainingType; t is not null; t = t.ContainingType)
                {
                    // v52 P2：包含类型非 partial 时报诊断——生成 partial 包裹声明与
                    // 用户非 partial 声明冲突报 CS0260 落 auto-generated 文件
                    // v53 P2：改报专用 PALENUM009（原复用 PALENUM007 的 {1} 是人为字符串
                    // 且"raise visibility"指引对该根因无效）；ValueType 携带实际包含类型名
                    // v61 勘正：v60 曾加 enum 包含类型前置拦截——经编译实证 C# 语法不允许
                    // enum 体内声明嵌套类型（CS1513），ContainingType 链不可能出现 enum，
                    // 该分支是不可构造路径的死代码，已回退（D 片 v60 P3-1 场景为推断幻觉）
                    if (!t.IsPartial(ct))
                    {
                        return new EnumGenInfo(
                            Namespace: GetNamespaceName(classSymbol),
                            TypeName: classSymbol.Name,
                            ContainingDeclarations: [],
                            ContainingNames: [],
                            ValueType: t.Name,
                            Fields: [],
                            HasFields: false,
                            DiagnosticId: "PALENUM009",
                            Location: context.TargetNode.GetLocation());
                    }
                    var kind = t.IsRecord
                        ? (t.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                        : t.TypeKind == TypeKind.Struct ? "partial struct"
                        : t.TypeKind == TypeKind.Interface ? "partial interface"
                        : "partial class";
                    // v41 P1：补 Interface 腿——interface 嵌套类型（C# 合法）落 partial class
                    // 产出同名义声明种类冲突（CS0101/CS0261 落 auto-generated 文件）
                    // v62 P3：泛型包含类型已被 PALENUM004 前置拦截，此处 Arity 恒 0、
                    // arity 拼接不可达——保留供未来放宽拦截时复用
                    var arity = t.Arity > 0
                        ? $"<{string.Join(", ", t.TypeParameters.Select(pr => pr.Name))}>"
                        : "";
                    containingDeclarations.Insert(0, $"{kind} {t.Name}{arity}");
                    containingNames.Insert(0, t.Name);
                }

                // 编译时 walk 语法树：收集所有 partial 声明中的 public/internal static 字段
                // ITM-070：字段可能拆分在多个 partial 文件——用 Symbol 的全部声明引用遍历，
                // 而非仅 TargetNode（带 attribute 的那个声明），否则跨文件字段被遗漏，
                // 触发 PALENUM001 且不生成注册代码。
                var fields = ImmutableArray.CreateBuilder<string>();
                foreach (var reference in classSymbol.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(ct) is not ClassDeclarationSyntax partialDecl)
                        continue;

                    // v22 D1 修复：跨文件 partial 时 reference.GetSyntax 返回另一棵语法树的节点，
                    // context.SemanticModel 绑定 attribute 所在树——CheckSyntaxNode 对树外节点抛
                    // ArgumentException → CS8785 生成器崩溃（探针实证：CrossFilePartial 测试红）。
                    // 改用 compilation.GetSemanticModel(node.SyntaxTree)——per-tree 获取正确模型。
                    // ITM-222 语义不变：仍只收集类型为目标 SmartEnum 自身的字段。
                    var semanticModel = context.SemanticModel.Compilation.GetSemanticModel(partialDecl.SyntaxTree);

                    foreach (var member in partialDecl.Members)
                    {
                        if (member is FieldDeclarationSyntax fds
                            && fds.Modifiers.Any(SyntaxKind.StaticKeyword)
                            && (fds.Modifiers.Any(SyntaxKind.PublicKeyword) || fds.Modifiers.Any(SyntaxKind.InternalKeyword)))
                        {
                            foreach (var variable in fds.Declaration.Variables)
                            {
                                var fieldSymbol = semanticModel.GetDeclaredSymbol(variable, ct) as IFieldSymbol;
                                if (fieldSymbol is null) continue;

                                // 字段声明类型必须等于目标类型自身（排除 int Version / string Name 等辅助字段）
                                if (SymbolEqualityComparer.Default.Equals(fieldSymbol.Type, classSymbol))
                                {
                                    fields.Add(variable.Identifier.Text);
                                }
                            }
                        }
                    }
                }

                if (fields.Count == 0)
                {
                    // 有 [GenerateEnum] 但无静态字段 — 返回空字段信息以触发警告
                    // P3 修复（二十一轮）：携带定位——原 Location.None 使 PALENUM001 无处
                    // 指示（IDE 无 squiggle、SARIF 无精确位置）；不参与相等（位置随编辑漂移）
                    return new EnumGenInfo(
                        Namespace: GetNamespaceName(classSymbol),
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        ValueType: valueType.ToDisplayString(),
                        Fields: [],
                        HasFields: false,
                        Location: context.TargetNode.GetLocation());
                }

                return new EnumGenInfo(
                    GetNamespaceName(classSymbol),
                    classSymbol.Name,
                    [.. containingDeclarations],
                    [.. containingNames],
                    valueType.ToDisplayString(),
                    fields.ToImmutable(),
                    HasFields: true,
                    // P2 修复（二十一轮）：携带定位供 PALENUM005（重复 partial 声明）指示
                    // 重复挂 attribute 的具体声明——不参与相等（位置随编辑漂移）
                    Location: context.TargetNode.GetLocation());
            })
            .WithTrackingName("EnumGenerator_Candidates")
            .Where(static info => info is not null)!;

        // 步骤 2：有字段时生成，无字段时报告警告
        // P2 修复（二十一轮）：双 partial 声明崩溃——Collect 后单次回调内去重（方案分支
        // 说明见 IdentityGenerator 同名修复：闭包 HashSet 跨增量 pass 残留会误报）
        context.RegisterSourceOutput(candidates.Collect(), static (spc, infos) =>
        {
            var seenHints = new HashSet<string>();
            foreach (var info in infos)
            {
                if (info.DiagnosticId is not null)
                {
                    // P2 修复：隔层继承报 PALENUM002（Error 级）
                    // P3 修复（八轮评审）：record 声明报 PALENUM003——按 DiagnosticId 分派
                    // P3 修复（十七轮）：泛型声明报 PALENUM004
                    // v26 P3 生成器族：非 partial 声明报 PALENUM006
                    var descriptor = info.DiagnosticId switch
                    {
                        "PALENUM003" => RecordNotSupportedError,
                        "PALENUM004" => GenericDeclarationNotSupported,
                        "PALENUM006" => NonPartialDeclarationError,
                        "PALENUM007" => NonAccessibleDeclarationError,
                        // v35 P3（DA4）：struct/interface 声明引导诊断
                        "PALENUM008" => StructOrInterfaceNotSupportedError,
                        // v53 P2：非 partial 包含类型（{1} = 实际包含类型名）
                        "PALENUM009" => ContainingTypeNotPartialError,
                        _ => NotDirectInheritanceError,
                    };
                    spc.ReportDiagnostic(Diagnostic.Create(
                        descriptor,
                        info.Location ?? Location.None,
                        info.TypeName,
                        info.ValueType));
                    continue;
                }
                if (!info.HasFields)
                {
                    // 报告警告而非静默跳过
                    // P3 修复（二十一轮）：PALENUM001 用 transform 携带的定位（原 Location.None）
                    spc.ReportDiagnostic(Diagnostic.Create(
                        NoFieldsWarning,
                        info.Location ?? Location.None,
                        info.TypeName));
                    continue;
                }
                // P3 修复（八轮评审）：hint 名的嵌套类型链改用 ContainingNames（纯类型名）——
                // 原 ContainingDeclarations.Split(' ').Last() 对泛型嵌套类型产出含 "<T>"
                // 的非法字符（如 "Outer<int>"），与 IdentityGenerator 风格对齐；
                // 全局命名空间（Namespace == null）用 "_" 仅作 hint 名保底
                // 三十八轮 P3 修复：hint 拼接对齐 IdentityGenerator ITM-098 的 "+" 方案——
                // 命名空间内的 "." 保留原样，嵌套层级用 "+" 显式编码（"+" 非 C# 标识符字符，
                // 不可能出现在类型名中），彻底消除 A_B.C 与 A.B_C 病态命名的同 hint 碰撞
                // （原 "_" 连接方案 AddSource 重复 hint 抛 ArgumentException）。hint 仅作
                // 生成物文件名与去重键，变更不影响编译。
                var hint = info.ContainingDeclarations.Length > 0
                    ? $"{(info.Namespace is null ? "" : info.Namespace + "+")}{string.Join("+", info.ContainingNames)}.{info.TypeName}.g.cs"
                    : $"{(info.Namespace is null ? "" : info.Namespace + ".")}{info.TypeName}.g.cs";
                if (!seenHints.Add(hint))
                {
                    // P2 修复（二十一轮）：重复 [GenerateEnum] 声明——仅首个声明生成代码，
                    // 其余报 PALENUM005 而非让 AddSource 抛 ArgumentException 崩溃
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DuplicatePartialDeclaration,
                        info.Location ?? Location.None,
                        info.TypeName));
                    continue;
                }
                spc.AddSource(hint, GenerateEnumCode(info));
            }
        });
    }

    // P3 修复（八轮评审）：全局命名空间不再 fallback "_"——旧值产出 "namespace _;"
    // 使生成物落入 _ 命名空间与用户类型不合并；返回 null 由 emit 侧条件包裹
    private static string? GetNamespaceName(INamedTypeSymbol symbol)
        => symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString()
            : null;

    // v26 P3 生成器族：PalDDD.Core.SmartEnum<TSelf, TValue> 的符号语义判定——
    // Name + 命名空间链逐级比对（PalDDD.Core 直属 global）+ Arity==2，
    // 不经 ToDisplayString()（其输出在 extern alias 下带别名前缀使精确比对失配）
    private static bool IsPalSmartEnumBase(INamedTypeSymbol originalDefinition)
        => originalDefinition.Name == "SmartEnum"
           && originalDefinition.Arity == 2
           && originalDefinition.ContainingNamespace is
           {
               Name: "Core",
               ContainingNamespace: { Name: "PalDDD", ContainingNamespace.IsGlobalNamespace: true }
           };

    // P3 修复（十七轮）：沿 ContainingType 链检测泛型包含类型——
    // [ModuleInitializer] 不允许位于泛型类型成员，生成物必然编译失败
    private static bool IsWithinGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.Arity > 0)
                return true;
        }

        return false;
    }

    // v35 P3（DA1）：AccessibilityToModifierText 私有副本已提取至 GeneratorAccessibility
    // 共享（PALENUM007/PALID006/PALMSG007 三生成器统一消费），本文件改调共享实现。

    /// <summary>生成硬编码字段引用的静态构造函数——零反射，100% AOT 兼容</summary>
    private static string GenerateEnumCode(EnumGenInfo info)
    {
        // 构建字段列表：Field1, Field2, Field3
        var fieldList = string.Join(",\n            ", info.Fields);
        // P2 修复：嵌套类型按 ContainingType 链包 partial 声明（零嵌套时 open/close 为空，输出与旧版一致）
        var open = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(d => $"{d}\n{{")) + "\n"
            : "";
        var close = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(_ => "}")) + "\n"
            : "";

        // P3 修复（八轮评审）：全局命名空间（Namespace == null）不生成 namespace 声明——
        // 旧 fallback "_" 产出 "namespace _;" 使生成物落入 _ 命名空间与用户类型不合并
        var nsDecl = info.Namespace is null ? "" : $"namespace {info.Namespace};\n";

        return $$"""
// <auto-generated/>
using System.Runtime.CompilerServices;

{{nsDecl}}{{open}}
partial class {{info.TypeName}}
{
    /// <summary>编译时值注册——所有字段引用均为硬编码，零反射，完全 AOT 兼容</summary>
    [ModuleInitializer]
    internal static void __PalRegisterValues()
    {
        RegisterValues([
            {{fieldList}}
        ]);
    }
}{{close}}
""";
    }

    private sealed record EnumGenInfo(
        string? Namespace,
        string TypeName,
        string[] ContainingDeclarations,
        string[] ContainingNames,
        string ValueType,
        ImmutableArray<string> Fields,
        bool HasFields = true,
        string? DiagnosticId = null,
        Location? Location = null)
    {
        // P3 修复（八轮评审）：数组/ImmutableArray 字段默认引用相等破坏增量管线缓存
        // （每次编译新实例 → 缓存恒 miss）——逐元素比较实现值等价，镜像
        // MessageRegistryGenerator.LocationInfo 的 value-equatable 范式。
        // P3 修复（十七轮）：DiagnosticMessage 死字段已删——诊断消息由 descriptor 统一
        // 承载，payload 从不消费该字段。
        // P3 修复（十八轮验证轮 A）：DiagnosticId 纳入相等——ReportDiagnostic 也是管线输出，
        // PALENUM002/003/004 翻转而其余字段相等时缓存命中会残留 IDE 僵尸诊断
        // （镜像 IdGenInfo 十轮修法；Location 仍不参与：位置随编辑漂移，参与会造成缓存 miss）。
        public bool Equals(EnumGenInfo? other) =>
            other is not null
            && Namespace == other.Namespace
            && TypeName == other.TypeName
            && ContainingDeclarations.SequenceEqual(other.ContainingDeclarations)
            && ContainingNames.SequenceEqual(other.ContainingNames)
            && ValueType == other.ValueType
            && FieldsEqual(Fields, other.Fields)
            && HasFields == other.HasFields
            && DiagnosticId == other.DiagnosticId;

        // 手写逐元素循环：避免 SequenceEqual 扩展方法在 ImmutableArray 与
        // System.Linq.ImmutableArrayExtensions 之间的绑定歧义（后者可能退化为引用比较）
        private static bool FieldsEqual(ImmutableArray<string> left, ImmutableArray<string> right)
        {
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
                hash = hash * 31 + TypeName.GetHashCode();
                foreach (var declaration in ContainingDeclarations) hash = hash * 31 + declaration.GetHashCode();
                foreach (var containingName in ContainingNames) hash = hash * 31 + containingName.GetHashCode();
                hash = hash * 31 + ValueType.GetHashCode();
                foreach (var field in Fields) hash = hash * 31 + field.GetHashCode();
                hash = hash * 31 + HasFields.GetHashCode();
                hash = hash * 31 + (DiagnosticId?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}


/// <summary>INamedTypeSymbol 扩展（v52 P2：包含类型 partial 性检查共享）。</summary>
internal static class NamedTypeSymbolExtensions
{
    /// <summary>判断命名类型声明（任意层级，与是否在命名空间内无关）是否含 partial 修饰符。</summary>
    public static bool IsPartial(this INamedTypeSymbol type, CancellationToken ct = default)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax baseDecl
                && baseDecl.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }
        return false;
    }
}
