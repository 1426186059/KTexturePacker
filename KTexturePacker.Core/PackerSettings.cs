namespace KTexturePacker.Core;

/// <summary>
/// 图集打包参数。
/// </summary>
public sealed class PackerSettings
{
    /// <summary>图集最大边长（会按 2 的幂从能容纳最小精灵的尺寸往上试探，直到该上限）。</summary>
    public int MaxSize { get; init; } = 2048;

    /// <summary>精灵之间的留白（像素）。</summary>
    public int Padding { get; init; } = 1;

    /// <summary>是否允许 90° 旋转以换取更高填充率。</summary>
    public bool AllowRotation { get; init; }

    /// <summary>使用的装箱启发式算法。</summary>
    public MaxRectsMethod Algorithm { get; init; } = MaxRectsMethod.BestShortSideFit;
}
