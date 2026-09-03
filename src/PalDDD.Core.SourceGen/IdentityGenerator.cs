using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.SourceGen;


// ─────────────────────────────────────────────────────────────
// 源码生成器 — 强类型 ID
// ─────────────────────────────────────────────────────────────

[Generator(LanguageNames.CSharp)]
public sealed class IdentityGenerator : IIncrementalGenerator
{
    private const string AttributeName = "PalDDD.Core.GenerateIdAttribute";

    // P3 修复（八轮评审）：非白名单 IdType 从"生成永不成功的 TryParse"改为编译期诊断，
    // 仿 MessageRegistryGenerator/EnumGenerator 的 PALMSG/PALENUM 诊断模式
    private static readonly DiagnosticDescriptor UnsupportedIdSourceType = new(
        "PALID001",
        "GenerateId source type is not supported",
        "Type '{0}' uses [GenerateId] with unsupported source type '{1}'. Supported source types: System.Guid, ByteAether.Ulid.Ulid, int (Int32), long (Int64), string.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P3 修复（九轮评审）：非 partial record struct 声明从"静默跳过"改为编译期诊断——
    // 静默让错误延迟到使用点 CS0117（无指向性），与 PALENUM003 的反馈哲学对齐
    private static readonly DiagnosticDescriptor NonPartialRecordStructDeclaration = new(
        "PALID002",
        "GenerateId target must be a partial record struct",
        "Type '{0}' uses [GenerateId] but is not declared as a partial record struct. Declare it as 'partial record struct' so the generator can merge generated members (readonly optional — the generated part makes the whole struct readonly; do not declare non-readonly instance fields, CS8340).",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）生成坏代码——
    // 生成物中 namespace 级 TypeConverter/JsonConverter 以裸名引用嵌套 ID（泛型外层
    // 无类型参数可用，typeof(Outer.Foo) 编译失败）；自身泛型时生成物裸名声明与用户
    // partial record struct Foo<T> 同名冲突。编译期报 PALID003 引导移出泛型声明。
    private static readonly DiagnosticDescriptor GenericDeclarationNotSupported = new(
        "PALID003",
        "GenerateId does not support generic declarations",
        "Type '{0}' uses [GenerateId] within a generic declaration. Generic identities are not supported; move the target out of the generic type or remove its type parameters.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P2 修复（二十一轮）：同一类型的多个 partial 声明均挂 [GenerateId]——
    // ForAttributeWithMetadataName 每声明触发一次 transform，两个 candidate 算出
    // 相同 hint，AddSource 同 hint 第二次调用抛 ArgumentException 使整个生成器
    // 崩溃（连带丢失本生成器全部生成物）。重复声明报 PALID004，仅首个声明生成代码。
    private static readonly DiagnosticDescriptor DuplicatePartialDeclaration = new(
        "PALID004",
        "GenerateId attribute declared on multiple partial declarations",
        "Type '{0}' has [GenerateId] applied to multiple partial declarations. Apply the attribute to only one partial declaration.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ITM-074 修复：构造参数为 null（[GenerateId(null)]）时 transform 直接 NRE——
    // NRE 从增量生成器冒泡会毁掉整个编译的全部生成物（与 PALID004 崩溃同害）。
    // 编译期报 PALID005 引导修正，不崩溃。
    private static readonly DiagnosticDescriptor NullSourceType = new(
        "PALID005",
        "GenerateId source type must not be null",
        "Type '{0}' uses [GenerateId] with a null source type. Pass a supported type: System.Guid, ByteAether.Ulid.Ulid, int (Int32), long (Int64), string.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v26 P3 生成器族：PALID005 消息区分两种情形——非 null 但非 INamedTypeSymbol 的
    // 源类型（泛型参数 [GenerateId(typeof(T))] / 数组 typeof(int[]) 等）原与 null 同报
    // "null source type"（非 null 却称 null，误导排障方向）；诊断 Id 不变（PALID005），
    // 与 NullSourceType 共用 Id 但消息各自指向根因
    private static readonly DiagnosticDescriptor NonNamedSourceType = new(
        "PALID005",
        "GenerateId source type must be a named type",
        "Type '{0}' uses [GenerateId] with source type '{1}', which is not a named type. Pass a supported named type: System.Guid, ByteAether.Ulid.Ulid, int (Int32), long (Int64), string.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v33 P3：private/protected nested 类型挂 [GenerateId] 时，生成物的 namespace 级
    // TypeConverter/JsonConverter 以裸名引用该类型——可访问性低于 internal 的嵌套类型
    // 对生成物不可见（CS0122 落在 auto-generated 文件，排障困难）。编译期报 PALID006
    // 不生成坏代码（镜像 EnumGenerator PALENUM007）。v34 P3 勘正：原"顶层类型恒
    // public/internal 不受影响"失实——生成 partial 硬编码 public，internal 声明（含顶层）
    // 放行后与生成物合并报 CS0262，internal 非合法目标，消息单腿引导升 public。
    // v37 P2 统一口径：internal 声明编译必炸 CS0262——Public-only 阈值不放行任何坏输入
    //（v36 收紧阈值仍放行 internal 的"误伤 internal 顶层合法场景"顾虑已被双向探针证伪：
    // 不存在可工作的 internal 用法）
    // v35 P3（DA1）：v34 链检查升格后原消息 "is not at least internal" 仍描述目标自身——
    // 链中间层阻断场景（public Outer → private Mid → public Foo）目标自身 public，消息
    // 失实且"raise the declaration"指引无效（改目标自身救不了 Mid）。消息改两参：{1} 经
    // BlockingAccessibilityText 携带实际阻断层修饰符文本（GeneratorAccessibility 共享
    // helper），措辞改为"declaration or its containing type chain blocks visibility"形态；
    // public 单腿保留但补链语义（"and its containing types"）
    // v37 P3：解释腿改双因通用措辞——原 "declarations below internal are invisible" 只描述
    // 链可见性腿（CS0122），与 v34/v36 陆续补入的 partial 合并冲突腿（CS0262）失配——
    // internal 自身声明被拦的真因是 partial 合并冲突而非不可见。统一为"不能与生成的
    // public 声明合并（可访问性低于 internal 或 partial 访问性冲突）"双因措辞
    private static readonly DiagnosticDescriptor NonAccessibleDeclaration = new(
        "PALID006",
        "GenerateId does not support inaccessible declarations",
        "Type '{0}' uses [GenerateId] but its declaration or its containing type chain cannot merge with the generated public declaration (blocking declaration is '{1}'; visibility below internal or partial accessibility conflict). Raise the declaration (and its containing types) to public.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    // v36 P2：Identity 侧在链检查通过后追加自身声明收紧（见 transform 内 if 块）——
    // GeneratorAccessibility 的 ProtectedOrInternal 放行对 Enum/Message 姊妹成立（其生成物
    // 不带访问修饰符，与用户声明合并无冲突），对 Identity 不成立：生成物硬编码
    // public readonly partial record struct，用户 protected internal 声明与之合并报 CS0262
    //（net11 探针双向实证）。消息沿用本 descriptor 现有形态（{1} = 自身修饰符文本）。
    // v37 P2：阈值进一步收紧为 Public-only——v36 的 public/internal 阈值仍放行 internal，
    // 而 internal 声明与硬编码 public 生成物合并同样必报 CS0262（双向探针实证），不存在
    // 可工作的 internal 用法；v36 保留 internal 的"误伤 internal 顶层合法场景"顾虑已被证伪

    // v53 P1（D 片）：非 partial 包含类型专用诊断——v52 引入该检查时 DiagnosticId 置
    // "PALID007" 但既未建 descriptor 也未在分派 switch 加 case，实际落 default 报
    // PALID001 "unsupported source type"（错误指引：真实修复是给包含类型加 partial，
    // 与 IdType 白名单无关，Guid 恰在白名单内时指引彻底反向）
    private static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        "PALID007",
        "GenerateId requires partial containing types",
        "Type '{0}' uses [GenerateId] but its containing type '{1}' is not partial — the generated partial declaration cannot merge with it (CS0260 would land in auto-generated files). Add 'partial' to the containing declaration.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            predicate: static (node, _) => IsStructKindDeclaration(node),
            transform: static (context, ct) =>
            {
                var structSymbol = (INamedTypeSymbol)context.TargetSymbol;
                var attrData = context.Attributes[0];
                // ITM-074 修复：构造参数缺失/为 null（[GenerateId(null)]）时
                // 原代码 (INamedTypeSymbol)null! 在下方 ToDisplayString() 处 NRE，
                // 从增量生成器冒泡毁掉整个编译的全部生成物。先判 null 报 PALID005。
                // v26 P3 生成器族：null 与非 null 非 NamedType 分开携带——SourceType 置
                // 实际类型显示名（null/缺参时空串），分派侧据此选择 PALID005 的两种消息；
                // 非 null 的 ITypeParameterSymbol（typeof(T)）/IArrayTypeSymbol（typeof(int[])）
                // 此前被同一模式吞掉误报 "null source type"
                if (attrData.ConstructorArguments.Length == 0
                    || attrData.ConstructorArguments[0].Value is not INamedTypeSymbol sourceType)
                {
                    var nonNullSourceTypeDisplay = attrData.ConstructorArguments.Length > 0
                        && attrData.ConstructorArguments[0].Value is ITypeSymbol nonNamedType
                            ? nonNamedType.ToDisplayString()
                            : "";
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: nonNullSourceTypeDisplay,
                        IsNumeric: false,
                        DiagnosticId: "PALID005",
                        Location: context.TargetNode.GetLocation());
                }

                // P3 修复（九轮评审）：非 partial record struct 声明报 PALID002——
                // 生成物恒为 partial record struct，普通 struct / 非 partial 声明无法合并
                // v28 P3：GetSyntax 补传 ct（对齐同文件族 EnumGenerator partial 收集路径与
                // MessageRegistryGenerator.ApplicationSyntaxReference 形态）——增量管线取消信号
                // 可传播到语法物化，无参重载在取消后仍拉取语法节点
                var isPartialRecordStruct = structSymbol.IsRecord
                    && structSymbol.DeclaringSyntaxReferences.Any(r =>
                        r.GetSyntax(ct) is TypeDeclarationSyntax d
                        && d.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword)));
                if (!isPartialRecordStruct)
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID002",
                        Location: context.TargetNode.GetLocation());
                }

                // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）暂不支持
                // （见 GenericDeclarationNotSupported 注释）——编译期报 PALID003，不生成坏代码
                if (structSymbol.Arity > 0 || IsWithinGenericContainingType(structSymbol))
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID003",
                        Location: context.TargetNode.GetLocation());
                }

                // v33 P3：可访问性拦截——可访问性低于 internal（private/protected 等）的
                // nested 类型对生成物的 namespace 级 converter 不可见（裸名引用必 CS0122）。
                // 编译期报 PALID006 不生成坏代码。
                // v34 P2：检查升格为 ContainingType 全链（GeneratorAccessibility 共享 helper）。
                // v37 P2 勘正（原 v34"已知残余"消除）：v34 曾标注"链可见但自身 internal 的
                // 声明放行后与生成物合并报 CS0262、Public-only 阈值会误伤 internal 顶层合法
                // 场景"——该顾虑已被双向探针证伪：internal 声明与硬编码 public 生成物合并
                // 编译必炸 CS0262，不存在可工作的 internal 用法；拦截阈值已收紧为 Public-only
                //（见下方 v37 自身声明检查），本残余不再存在
                // v35 P3（DA1）：捕获阻断层可访问性并经 BlockingAccessibilityText 携带——
                // 链中间层阻断（public Outer → private Mid → public Foo）时消息 {1} 显示
                // 实际阻断层（Mid）的修饰符，不再失实描述目标自身
                if (GeneratorAccessibility.GetBlockingAccessibility(structSymbol) is { } blockingAccessibility)
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID006",
                        BlockingAccessibilityText: GeneratorAccessibility.AccessibilityToModifierText(blockingAccessibility),
                        Location: context.TargetNode.GetLocation());
                }

                // v36 P2→v37 P2：链检查通过后追加自身声明收紧，阈值 Public-only——
                // 依据（与 Enum/Message 姊妹的差异）：二者生成物（partial class / partial 声明）
                // 不带访问修饰符、无 partial 合并冲突，ProtectedOrInternal 放行对其成立；
                // Identity 生成物硬编码 public readonly partial record struct（见
                // GenerateIdentityCode 模板），用户声明与之一旦访问性不同（protected internal
                // 或 internal）合并即报 CS0262（net11 探针双向实证）——internal 声明编译必炸
                // CS0262，Public-only 阈值不放行任何坏输入，不存在被"误伤"的合法 internal
                // 用法。private/protected/private protected 已被上方链检查先报，本检查
                // 新增拦截 ProtectedOrInternal 与 Internal
                if (structSymbol.DeclaredAccessibility is not Accessibility.Public)
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID006",
                        BlockingAccessibilityText: GeneratorAccessibility.AccessibilityToModifierText(structSymbol.DeclaredAccessibility),
                        Location: context.TargetNode.GetLocation());
                }

                // P2 修复：嵌套类型——ContainingNamespace 不含类型层级，生成物需按
                // ContainingType 链包 partial 声明；否则 namespace 级平铺的同名类型
                // 与用户声明的嵌套 partial 不合并（Outer.Inner 得不到 IPalIdentity 实现）。
                var containingDeclarations = new List<string>();
                var containingNames = new List<string>();
                for (var t = structSymbol.ContainingType; t is not null; t = t.ContainingType)
                {
                    // v52 P2：包含类型非 partial 时报诊断——生成 partial 包裹声明与用户
                    // 非 partial 声明冲突报 CS0260 落 auto-generated 文件无排障指引
                    // v53 P1：SourceType 字段复用为携带非 partial 包含类型名（分派侧 {1}）
                    // v62 P3：形态对齐 EnumGenerator（原 DeclaredAccessibility 前置对
                    // 命名类型恒真——ContainingType 成员不取 NotApplicable）
                    if (!t.IsPartial(ct))
                    {
                        return new IdGenInfo(
                            Namespace: null,
                            TypeName: structSymbol.Name,
                            ContainingDeclarations: [],
                            ContainingNames: [],
                            SourceType: t.Name,
                            IsNumeric: false,
                            DiagnosticId: "PALID007",
                            Location: context.TargetNode.GetLocation());
                    }
                    var kind = t.IsRecord
                        ? (t.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                        : t.TypeKind == TypeKind.Struct ? "partial struct"
                        : t.TypeKind == TypeKind.Interface ? "partial interface"
                        : "partial class";
                    // v41 P1：补 Interface 腿——interface 嵌套类型（C# 合法）落 partial class
                    // 产出同名义声明种类冲突（CS0101/CS0261 落 auto-generated 文件）
                    // v64 P3：泛型包含类型已被 PALID003 前置拦截，此处 Arity 恒 0、arity
                    // 拼接不可达——保留供未来放宽拦截时复用（对齐 EnumGenerator v62 注释）
                    var arity = t.Arity > 0
                        ? $"<{string.Join(", ", t.TypeParameters.Select(p => p.Name))}>"
                        : "";
                    containingDeclarations.Insert(0, $"{kind} {t.Name}{arity}");
                    containingNames.Insert(0, t.Name);
                }

                // P3 修复（八轮评审）：全局命名空间不再 fallback "_"——旧值产出
                // "namespace _;" 使生成物落入 _ 命名空间，与用户的全局类型不合并；
                // null 时 emit 侧不生成 namespace 声明
                var namespaceName = structSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNs
                    ? containingNs.ToDisplayString()
                    : null;

                // P3 修复（八轮评审）：白名单外 IdType 报 PALID001——原实现静默生成
                // "result = default; return false;" 的恒失败 TryParse，用户无编译期反馈
                // P1 修复（十七轮）：Ulid case 此前写 "ByteAether.Ulid" 永不匹配——ToDisplayString()
                // 返回 "ByteAether.Ulid.Ulid"（命名空间 ByteAether.Ulid + 类型名 Ulid），
                // [GenerateId(typeof(Ulid))] 恒报 PALID001（诊断消息声称支持的正是它拒绝的类型）。
                // 编译探针实证；十六轮未发现因测试零 Ulid/long 用例。
                // v25 P3 生成器族：白名单比对改符号语义——ToDisplayString() 的字符串前缀
                // 剥离（Replace("global::","")）对 extern alias 前缀（"alias::Ns.Type"）失效
                // 使精确匹配误报 PALID001；int/long 的同型失配 v8 已用 SpecialType 修复
                //（见下方 IsNumeric 注释），本处推广到全部白名单——int/long/string 走
                // SpecialType（基元类型），Guid/Ulid 用 Name + 命名空间链判定（SpecialType
                // 枚举无 System_Guid——Guid 非基元类型；Symbol.Name 不含别名前缀，不受
                // extern alias 影响）。extern alias 测试桩：见
                // SourceGeneratorDirectTests.IdentityGenerator_UlidViaExternAlias。
                var normalizedSourceType = sourceType.SpecialType switch
                {
                    Microsoft.CodeAnalysis.SpecialType.System_Int32 => "int",
                    Microsoft.CodeAnalysis.SpecialType.System_Int64 => "long",
                    Microsoft.CodeAnalysis.SpecialType.System_String => "string",
                    _ => IsSystemGuid(sourceType) ? "Guid"
                        : IsByteAetherUlid(sourceType) ? "Ulid"
                        : null
                };
                if (normalizedSourceType is null)
                {
                    return new IdGenInfo(
                        Namespace: namespaceName,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [.. containingDeclarations],
                        ContainingNames: [.. containingNames],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID001",
                        Location: context.TargetNode.GetLocation());
                }

                return new IdGenInfo(
                    namespaceName,
                    structSymbol.Name,
                    [.. containingDeclarations],
                    [.. containingNames],
                    normalizedSourceType,
                    // v8 评审：SpecialType 判定替代命名空间字符串比对——extern alias 下 ToDisplayString
                    // 可能带别名前缀使 'System' 比对失配，int/long ID 静默丢 ++/-- 生成
                    sourceType.SpecialType is Microsoft.CodeAnalysis.SpecialType.System_Int32 or Microsoft.CodeAnalysis.SpecialType.System_Int64,
                    // P2 修复（二十一轮）：携带定位供 PALID004（重复 partial 声明）指示
                    // 重复挂 attribute 的具体声明——不参与相等（位置随编辑漂移）
                    Location: context.TargetNode.GetLocation());
            })
            .WithTrackingName("IdentityGenerator_Candidates")
            .Where(static info => info is not null)!;

        // P2 修复（二十一轮）：双 partial 声明崩溃——同一类型的两个 partial 声明均挂
        // [GenerateId] 时 ForAttributeWithMetadataName 每声明触发一次 transform，两个
        // candidate 计算出相同 hint，AddSource 同 hint 第二次调用抛 ArgumentException
        // 使整个生成器崩溃。方案分支：评审建议闭包 HashSet，但 RegisterSourceOutput
        // 回调在后续增量 pass 会对"值变化而 hint 不变"的条目重入（如 SourceType 从
        // Guid 改 int），跨 pass 残留的 HashSet 会误报 PALID004 并漏生成代码；改用
        // Collect() 后在单次回调内局部去重——去重状态只存在于一次调用内，无跨 pass 污染。
        context.RegisterSourceOutput(candidates.Collect(), static (spc, infos) =>
        {
            var seenHints = new HashSet<string>();
            foreach (var info in infos)
            {
                if (info.DiagnosticId is not null)
                {
                    // 诊断分派（九轮）：PALID001=源类型白名单外；PALID002=声明形式非 partial record struct
                    // P3 修复（十七轮）：PALID003=泛型声明（自身或包含类型）暂不支持
                    // ITM-074 修复：PALID005=构造参数为 null（[GenerateId(null)]）
                    switch (info.DiagnosticId)
                    {
                        case "PALID002":
                            spc.ReportDiagnostic(Diagnostic.Create(
                                NonPartialRecordStructDeclaration,
                                info.Location ?? Location.None,
                                info.TypeName));
                            break;
                        case "PALID003":
                            spc.ReportDiagnostic(Diagnostic.Create(
                                GenericDeclarationNotSupported,
                                info.Location ?? Location.None,
                                info.TypeName));
                            break;
                        case "PALID005":
                            // v26 P3 生成器族：SourceType 空 = null/缺参（原消息）；
                            // 非空 = 非 NamedType 源类型（typeof(T)/typeof(int[]) 等，
                            // 携带实际类型显示名）——两种根因各报各的消息，Id 不变
                            if (info.SourceType.Length == 0)
                            {
                                spc.ReportDiagnostic(Diagnostic.Create(
                                    NullSourceType,
                                    info.Location ?? Location.None,
                                    info.TypeName));
                            }
                            else
                            {
                                spc.ReportDiagnostic(Diagnostic.Create(
                                    NonNamedSourceType,
                                    info.Location ?? Location.None,
                                    info.TypeName,
                                    info.SourceType));
                            }
                            break;
                        case "PALID006":
                            // v33 P3：private/protected nested 声明对生成物不可见
                            // v35 P3（DA1）：{1} = 实际阻断层修饰符文本（BlockingAccessibilityText
                            // 携带）——链中间层阻断场景消息不再失实描述目标自身
                            spc.ReportDiagnostic(Diagnostic.Create(
                                NonAccessibleDeclaration,
                                info.Location ?? Location.None,
                                info.TypeName,
                                info.BlockingAccessibilityText));
                            break;
                        case "PALID007":
                            // v53 P1：非 partial 包含类型——{1} = 包含类型名（SourceType 携带），
                            // 指引加 partial 修饰符（此前落 default 报 PALID001 错误指引）
                            spc.ReportDiagnostic(Diagnostic.Create(
                                ContainingTypeNotPartial,
                                info.Location ?? Location.None,
                                info.TypeName,
                                info.SourceType));
                            break;
                        default:
                            // P3 修复（八轮评审）：PALID001——非白名单 IdType 编译期报错，不生成代码
                            spc.ReportDiagnostic(Diagnostic.Create(
                                UnsupportedIdSourceType,
                                info.Location ?? Location.None,
                                info.TypeName,
                                info.SourceType));
                            break;
                    }
                    continue;
                }

                var src = GenerateIdentityCode(info);
                // P3 修复（八轮评审）：全局命名空间（Namespace == null）用 "_" 仅作 hint 名保底，
                // 生成代码本身不再含 namespace 声明
                // ITM-098 修复（验证轮返工）：hint 拼接用 "+" 显式编码嵌套层级——命名空间内的
                // "." 保留原样（"A.B+C" 与 "A+B.C" 不再碰撞），"+" 非 C# 标识符字符，
                // 命名空间/类型名不可能包含，彻底消除同 hint 碰撞（PALID004 误报/AddSource 冲突）。
                // hint 仅作生成物文件名与去重键，变更不影响编译（测试均按 ".g.cs" 后缀匹配，
                // 见 SourceGeneratorDirectTests.GetGeneratedSource）；转换器类名仍用 "_" 拼接
                // （C# 标识符不允许 "+"，下划线边界撞名的 CS0101 属已声明限制）。
                var hint = info.ContainingNames.Length > 0
                    ? $"{(info.Namespace is null ? "" : info.Namespace + "+")}{string.Join("+", info.ContainingNames)}.{info.TypeName}.g.cs"
                    : $"{(info.Namespace is null ? "" : info.Namespace + ".")}{info.TypeName}.g.cs";
                if (!seenHints.Add(hint))
                {
                    // P2 修复（二十一轮）：重复 [GenerateId] 声明——仅首个声明生成代码，
                    // 其余报 PALID004 而非让 AddSource 抛 ArgumentException 崩溃
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DuplicatePartialDeclaration,
                        info.Location ?? Location.None,
                        info.TypeName));
                    continue;
                }

                spc.AddSource(hint, src);
            }
        });
    }

    // v25 P3 生成器族：Guid/Ulid 白名单的符号语义判定——Name + 命名空间链逐级比对，
    // 不经 ToDisplayString()（其输出可能带 extern alias 前缀）；同时限定外层命名空间
    // 直属 global，排除 Foo.System.Guid / Foo.ByteAether.Ulid 之类的嵌套误匹配
    private static bool IsSystemGuid(INamedTypeSymbol symbol)
        => symbol.Name == "Guid"
           && symbol.ContainingNamespace is
           {
               Name: "System",
               ContainingNamespace.IsGlobalNamespace: true
           };

    private static bool IsByteAetherUlid(INamedTypeSymbol symbol)
        => symbol.Name == "Ulid"
           && symbol.ContainingNamespace is
           {
               Name: "Ulid",
               ContainingNamespace: { Name: "ByteAether", ContainingNamespace.IsGlobalNamespace: true }
           };

    // P3 修复（九轮评审）：predicate 放宽到全部 struct 类声明（普通 struct + record struct），
    // 非 partial record struct 由 transform 报 PALID002——静默跳过让错误延迟到使用点 CS0117
    private static bool IsStructKindDeclaration(SyntaxNode node)
        => node is StructDeclarationSyntax
           || (node is RecordDeclarationSyntax r
               && r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));

    // P3 修复（十七轮）：沿 ContainingType 链检测泛型包含类型——
    // 生成物的 namespace 级 converter 无法以裸名引用泛型外层内的嵌套 ID
    private static bool IsWithinGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.Arity > 0)
                return true;
        }

        return false;
    }

    private static string GenerateIdentityCode(IdGenInfo info)
    {
        var name = info.TypeName;
        var srcType = info.SourceType;

        // P2 修复：嵌套类型——转换器类保持命名空间级，引用需带类型链限定名；
        // struct 本体按 ContainingType 链包 partial 声明（零嵌套时 open/close 为空，输出与旧版一致）
        var fullName = info.ContainingNames.Length > 0
            ? string.Join(".", info.ContainingNames) + "." + name
            : name;
        // P1 修复（五轮评审）：C# 类声明名不允许含点——转换器类名用下划线连接
        var converterName = info.ContainingNames.Length > 0
            ? string.Join("_", info.ContainingNames) + "_" + name
            : name;
        var open = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(d => $"{d}\n{{")) + "\n"
            : "";
        var close = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(_ => "}")) + "\n"
            : "";

        // Medium 修复（二十六轮验证轮 W3）：emit 别名 using——模板以裸 Ulid 作 srcType
        //（IPalIdentity<Ulid>/public Ulid Value），仅别名时无 global using 的真实消费方
        // 编译失败（7 个 CS0246/CS1503，harness 实测）。
        // v72 勘正：原同时 emit 非别名 using ByteAether.Ulid——v35 DA2 displayType 分派后
        // 生成模板内裸 Ulid 绝迹（全部经 PalUlid 别名/global::System.Guid 限定），非别名
        // using 零消费方已删（D 片 grep 实证）
        var ulidUsing = srcType == "Ulid" ? "\r\nusing PalUlid = ByteAether.Ulid.Ulid;" : "";

        // v35 P3（DA2）：模板对 Ulid/Guid 输出裸类型名——用户命名空间含同名类型（class Ulid/
        // class Guid）时裸名解析被遮蔽，生成物编译失败（CS0246/CS1503 落在用户侧同名类型）。
        // Ulid 特化为 PalUlid 别名（上方 ulidUsing 的 using+using 双发已备，v24 机制）；
        // Guid 特化为 global::System.Guid 限定名；int/long/string 为基元关键字无遮蔽风险。
        // body 方法（New/TryParse/JsonRead 等）的分派键仍用 srcType，仅其输出文本内的
        // Guid 裸名同勘（见各方法内 global::System.Guid）
        var displayType = srcType switch
        {
            "Ulid" => "PalUlid",
            "Guid" => "global::System.Guid",
            _ => srcType
        };

        // P3 修复（八轮评审）：全局命名空间（Namespace == null）不生成 namespace 声明——
        // 旧 fallback "_" 产出 "namespace _;" 使生成物落入 _ 命名空间与用户类型不合并
        var nsDecl = info.Namespace is null ? "" : $"namespace {info.Namespace};\n";

        return $$"""
// <auto-generated/>
#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalDDD.Core;{{ulidUsing}}

{{nsDecl}}{{open}}
[TypeConverter(typeof({{converterName}}TypeConverter))]
[JsonConverter(typeof({{converterName}}JsonConverter))]
public readonly partial record struct {{name}} : IPalIdentity<{{displayType}}>, ISpanParsable<{{name}}>
{
    public {{displayType}} Value { get; init; }

    public static {{name}} New() => {{NewBody(srcType)}};
    public static {{name}} From({{displayType}} value) => {{FromBody(srcType)}};
    {{ToStringBody(srcType)}}
    public static bool TryParse(string? input, out {{name}} result)
    {
{{TryParseBody(srcType)}}
    }

    // ── ISpanParsable<T> 实现（AOT 安全，ASP.NET Core Minimal API 绑定兼容）──

    public static {{name}} Parse(string s, IFormatProvider? provider)
        => TryParse(s, out var r) ? r : throw new FormatException("Cannot parse '" + s + "' as " + typeof({{name}}).Name + ".");

    public static bool TryParse(string? s, IFormatProvider? provider, out {{name}} result)
        => TryParse(s, out result);

    public static {{name}} Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out var r) ? r : throw new FormatException("Cannot parse '" + s.ToString() + "' as " + typeof({{name}}).Name + ".");

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out {{name}} result)
    {
{{TryParseSpanBody(srcType)}}
    }
{{(info.IsNumeric ? NumericOperators(name, srcType) : "")}}
}{{close}}

internal sealed class {{converterName}}JsonConverter : JsonConverter<{{fullName}}>
{
    public override {{fullName}} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
{{JsonReadBody(srcType, fullName)}}
    }

    public override void Write(Utf8JsonWriter writer, {{fullName}} value, JsonSerializerOptions options)
    {
{{JsonWriteBody(srcType)}}
    }
}

internal sealed class {{converterName}}TypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof({{displayType}});

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s when {{fullName}}.TryParse(s, out var parsed) => parsed,
            {{displayType}} v => {{fullName}}.From(v),
            _ => throw new NotSupportedException()
        };
}
""";
    }

    private static string NewBody(string srcType) => srcType switch
    {
        // v35 P3（DA2）：Guid 裸名 → global::System.Guid——用户命名空间含同名 Guid 类型时
        // 裸名被遮蔽（Ulid 腿已用 PalUlid 别名，v26 W3）；分派键仍为白名单归一化值
        "Guid" => "new() { Value = global::System.Guid.NewGuid() }",
        "Ulid" => "new() { Value = PalUlid.New() }",
        // 数值/字符串类型 Id 由数据库或服务端分配，客户端 New() 无意义 —— 明确报错而非静默返回 default。
        _ => "throw new NotSupportedException(\"Numeric/string identities are assigned by the store; use From(value) instead.\")"
    };

    private static string FromBody(string srcType) => srcType switch
    {
        "string" => "!string.IsNullOrEmpty(value) ? new() { Value = value } : throw new ArgumentException(\"String identity value cannot be null or empty.\", nameof(value))",
        _ => "new() { Value = value }"
    };

    // v26 P3 生成器族：string Id 的 default 结构（Value == null）ToString 防 NRE——
    // 原模板统一生成 Value.ToString()!，null.ToString() 抛 NullReferenceException；
    // string 的 ToString() 返回自身，?? 空合并等价且零分配。值类型分支保持原样
    //（?? 对非可空值类型不编译，模板必须按 srcType 分支）
    private static string ToStringBody(string srcType) => srcType switch
    {
        "string" => "public override string ToString() => Value ?? string.Empty;",
        _ => "public override string ToString() => Value.ToString()!;"
    };

    private static string TryParseBody(string srcType) => srcType switch
    {
        // v35 P3（DA2）：Guid 裸名 → global::System.Guid（同 NewBody——遮蔽防护）
        "Guid" => "        if (global::System.Guid.TryParse(input, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "Ulid" => "        if (PalUlid.TryParse(input, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "int" => "        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "long" => "        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "string" => "        if (!string.IsNullOrEmpty(input)) { result = new() { Value = input }; return true; } result = default; return false;",
        _ => "        result = default; return false;"
    };

    private static string TryParseSpanBody(string srcType) => srcType switch
    {
        // v35 P3（DA2）：Guid 裸名 → global::System.Guid（同 NewBody——遮蔽防护）
        "Guid" => "        if (global::System.Guid.TryParse(s, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "Ulid" => "        if (PalUlid.TryParse(s, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "int" => "        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "long" => "        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "string" => "        if (!s.IsEmpty) { result = new() { Value = s.ToString() }; return true; } result = default; return false;",
        _ => "        result = default; return false;"
    };

    private static string JsonReadBody(string srcType, string name) => srcType switch
    {
        // v25 P3 生成器族：Guid/int/long 分支补 token 守卫——GetGuid/GetInt32/GetInt64 对
        // 不匹配 token 抛 InvalidOperationException，违反 S.T.J converter 契约（Read 的
        // 失败应以 JsonException 抛出，上层 catch (JsonException) 才能统一捕获；Ulid 分支
        // 已有同型守卫）。Guid 来自 String token，int/long 来自 Number token，守卫条件各按类型。
        // v30 P3 勘正：string 分支原仅靠 ?? throw 兜 Null token，Number/True 等非 String
        // token 仍从 GetString() 抛 InvalidOperationException——已补同型 token 守卫（见
        // string 分支注释），四值类型分支至此守卫族齐整。
        // v33 P2 补全：token 守卫只堵了类型半边——token 类型正确但格式非法时
        // GetGuid/GetInt32/GetInt64 仍抛 FormatException（Learn 官方文档独立异常条目），
        // 非 JsonException，契约破裂。三分支补 TryParse/TryGet 形态（镜像 Ulid 分支）。
        "Guid" => $"""
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Guid identity JSON value must be a JSON string.");
                // v35 P3（DA2）：Guid 裸名 → global::System.Guid（遮蔽防护，同 NewBody）
                if (!global::System.Guid.TryParse(reader.GetString(), out var guidValue))
                    throw new JsonException("Guid identity JSON value is not a valid Guid.");
                return {name}.From(guidValue);
        """,
        // 优化（二十五轮 API 扫描 B2）：读路径原为 GetString()（必然堆分配）+ Parse(string)——
        // 非转义字符串（绝大多数）直接 reader.ValueSpan（UTF-8 原始切片）TryParse 零分配；
        // 转义字符串回退 GetString()+Parse(string)。token 守卫保留原 JsonException-for-null
        // 语义（ValueSpan/ValueIsEscaped 仅对 String/PropertyName token 有效，Null token 原靠
        // GetString() 返 null 触发 JsonException，不守卫会退化成 InvalidOperationException）。
        // TryParse(ReadOnlySpan<byte>, IFormatProvider?, out Ulid) 已在 ByteAether.Ulid 1.4.0
        // net10 XML 证实。
        // v34 P2 注：本模板含字面块花括号（escaped 腿 if 块），插值定界符升格 $$（{name} →
        // {{name}}），字面 { } 单写——C# 11 raw interpolated string 规则（$""" 下无法写字面 {）
        "Ulid" => $$"""
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Ulid identity JSON value must be a JSON string (Ulid wire form).");
                if (reader.ValueIsEscaped)
                {
                    // v34 P2 补全（escaped 腿）：转义字符串无法走 ValueSpan 快路径，GetString 后
                    // 原 Parse 抛 FormatException（Ulid 1.4.0 XML 显式声明）——非 JsonException，
                    // S.T.J converter 契约破裂。改 TryParse 转 JsonException（对齐下方非转义腿）
                    if (!PalUlid.TryParse(reader.GetString()!, CultureInfo.InvariantCulture, out var escapedUlid))
                        throw new JsonException("Ulid identity JSON value is not a valid Ulid.");
                    return {{name}}.From(escapedUlid);
                }
                if (PalUlid.TryParse(reader.ValueSpan, null, out var ulid))
                    return {{name}}.From(ulid);
                throw new JsonException("Ulid identity JSON value is not a valid Ulid.");
        """,
        // v25 P3 生成器族：同 Guid 分支——Number token 守卫使坏 token（String/Null 等）抛
        // JsonException 而非 GetInt32/GetInt64 的 InvalidOperationException。
        // v33 P2 补全：TryGetInt32/TryGetInt64 兜格式腿（1.5/溢出等非法 Number token 由
        // GetInt32/GetInt64 抛 FormatException——非 JsonException）
        "int" => $"""
                if (reader.TokenType != JsonTokenType.Number)
                    throw new JsonException("Int32 identity JSON value must be a JSON number.");
                if (!reader.TryGetInt32(out var intValue))
                    throw new JsonException("Int32 identity JSON value is not a valid Int32.");
                return {name}.From(intValue);
        """,
        "long" => $"""
                if (reader.TokenType != JsonTokenType.Number)
                    throw new JsonException("Int64 identity JSON value must be a JSON number.");
                if (!reader.TryGetInt64(out var longValue))
                    throw new JsonException("Int64 identity JSON value is not a valid Int64.");
                return {name}.From(longValue);
        """,
        // v30 P3 生成器族：string 分支补 token 类型守卫（对齐 Guid/Ulid/int/long 分支）——
        // GetString() 对 Number/True/False/StartObject 等非 String token 抛
        // InvalidOperationException，违反 S.T.J converter 契约（Read 失败应以 JsonException
        // 抛出，上层 catch (JsonException) 才能统一捕获）。
        // v33 P3 勘正：v30 声称"?? throw 保留——Null token 时 GetString() 返回 null，
        // null 兜底仍需独立防线"不成立——上方 token 守卫已拦 Null token
        // （TokenType.Null != String 即抛），执行到 GetString() 时 TokenType 恒为 String、
        // 恒返回非 null，?? throw 为不可达死防线。删除改直接 GetString()!（对齐 Ulid 分支）。
        // v34 P2 补全（空串腿）：String token 但值为 "" 时 From 抛 ArgumentException
        //（FromBody 的 IsNullOrEmpty 守卫）——非 JsonException 契约破裂；写侧写出 "" 仅当
        // 用户显式 new() { Value = "" }（default 实例 Value==null，经 v27 的 WriteNullValue
        // 防御写 null token，不会写出 ""），roundtrip 读回即炸。空串转 JsonException
        //（对齐 TryParseBody 的 IsNullOrEmpty 判定语义）
        // v35 P3（DA3）勘正：上段 v34 原注"写侧对 default 绕过构造的实例会写出 ''"失实——
        // v27 写侧 null 防御（JsonWriteBody string 分支）已使 default 实例写 null token
        // 而非 ""；空串写出的唯一路径是用户显式以空串构造 Value
        "string" => $"""
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("String identity JSON value must be a JSON string.");
                var stringValue = reader.GetString()!;
                if (stringValue.Length == 0)
                    throw new JsonException("String identity JSON value cannot be empty.");
                return {name}.From(stringValue);
        """,
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    private static string JsonWriteBody(string srcType) => srcType switch
    {
        "Guid" => "        writer.WriteStringValue(value.Value);",
        // 优化（二十五轮 API 扫描 B1）：Ulid 写路径原为 value.Value.ToString()——每写一个 Id
        // 堆分配一个 26 字符字符串；Ulid 恒 26 字符（Crockford Base32），stackalloc 栈缓冲
        // TryFormat 后以 span 直接写出，零堆分配。TryFormat(Span<char>, out int,
        // ReadOnlySpan<char>, IFormatProvider) 已在 ByteAether.Ulid 1.4.0 net10 XML 证实；
        // 本生成器 netstandard2.0 仅 emit 文本，模板引用的 API 在用户项目（引用 Ulid 包）
        // 编译时解析——SourceGen 项目无需引用 Ulid。else 为防御回退（理论不可达）。
        "Ulid" => """
                Span<char> buffer = stackalloc char[26];
                if (value.Value.TryFormat(buffer, out int written, default, null))
                    writer.WriteStringValue(buffer[..written]);
                else
                    writer.WriteStringValue(value.Value.ToString()); // 理论不可达（26 字符恒足够）——防御回退
        """,
        "int" => "        writer.WriteNumberValue(value.Value);",
        "long" => "        writer.WriteNumberValue(value.Value);",
        // v27 P3 生成器族：string Id 的 default 结构（Value == null）显式 null 防御
        // （对齐 v26 ToStringBody 的 default 防御）——net11.0 实测 WriteStringValue(null)
        // 已写 null token 不抛 ANE，本分支为契约锁定（显式 null 语义，不依赖框架对
        // null 的隐式处理）。void 写方法不能套三元（分支类型 void 不编译），生成 if/else
        "string" => """
                if (value.Value is null)
                    writer.WriteNullValue();
                else
                    writer.WriteStringValue(value.Value);
        """,
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    // ITM-099 修复：++/-- 的加减法改 checked——原 unchecked 下 int.MaxValue 自增静默
    // 回绕为 int.MinValue（数据损坏无感知）；checked 抛 OverflowException 显式化。
    // 生成代码文本变化（+checked 包裹）不影响编译；测试仅断言含 "operator"。
    private static string NumericOperators(string name, string srcType) => $$"""

    public static {{name}} operator ++({{name}} value) => new() { Value = checked(({{srcType}})(value.Value + 1)) };
    public static {{name}} operator --({{name}} value) => new() { Value = checked(({{srcType}})(value.Value - 1)) };
""";

    private sealed record IdGenInfo(
        string? Namespace,
        string TypeName,
        string[] ContainingDeclarations,
        string[] ContainingNames,
        string SourceType,
        bool IsNumeric,
        string? DiagnosticId = null,
        // v35 P3（DA1）：PALID006 的阻断层修饰符文本（"private"/"protected"/…）——仅
        // 可访问性诊断分支携带，供诊断消息 {1} 显示实际阻断层（生成路径恒 null）。
        // 纳入相等：诊断也是管线输出，翻转而其余字段相等时缓存命中会残留 IDE 僵尸诊断
        //（镜像 DiagnosticId 十轮修法）
        string? BlockingAccessibilityText = null,
        Location? Location = null)
    {
        // P3 修复（八轮评审）：数组字段默认引用相等破坏增量管线缓存（每次编译新数组实例
        // → 引用不等 → 缓存恒 miss）——逐元素比较实现值等价，镜像 MessageRegistryGenerator
        // 的 LocationInfo value-equatable 范式。
        // P3 修复（十轮）：DiagnosticId 纳入相等——诊断也是输出，PALID001/PALID002 两态
        // 翻转而其余字段相等时，排除会使 IDE 增量编译回放过期诊断（Location 仍不参与：
        // 同一节点的位置随编辑漂移，参与相等会造成缓存 miss）。
        public bool Equals(IdGenInfo? other) =>
            other is not null
            && Namespace == other.Namespace
            && TypeName == other.TypeName
            && ContainingDeclarations.SequenceEqual(other.ContainingDeclarations)
            && ContainingNames.SequenceEqual(other.ContainingNames)
            && SourceType == other.SourceType
            && IsNumeric == other.IsNumeric
            && DiagnosticId == other.DiagnosticId
            && BlockingAccessibilityText == other.BlockingAccessibilityText;

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
                hash = hash * 31 + TypeName.GetHashCode();
                foreach (var declaration in ContainingDeclarations) hash = hash * 31 + declaration.GetHashCode();
                foreach (var containingName in ContainingNames) hash = hash * 31 + containingName.GetHashCode();
                hash = hash * 31 + SourceType.GetHashCode();
                hash = hash * 31 + IsNumeric.GetHashCode();
                hash = hash * 31 + (DiagnosticId?.GetHashCode() ?? 0);
                hash = hash * 31 + (BlockingAccessibilityText?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
