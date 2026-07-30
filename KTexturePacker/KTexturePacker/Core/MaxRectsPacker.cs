using System;
using System.Collections.Generic;

namespace KTexturePacker.Core;

/// <summary>
/// 一个矩形区域（打包后的占位框）。
/// </summary>
public readonly record struct PackerRect(int X, int Y, int Width, int Height);

/// <summary>
/// MaxRects 装箱算法实现（经典 Best Short/Long Side Fit、Bottom-Left、Contact-Point 启发式）。
/// 纯 C#，不依赖任何 UI / 图形库。
/// </summary>
public sealed class MaxRectsPacker
{
    private readonly int _binWidth;
    private readonly int _binHeight;
    private readonly bool _allowRotation;
    private readonly MaxRectsMethod _method;
    private readonly List<PackerRect> _freeRectangles = new();
    private readonly List<PackerRect> _usedRectangles = new();

    public MaxRectsPacker(int width, int height, bool allowRotation,
        MaxRectsMethod method = MaxRectsMethod.BestShortSideFit)
    {
        _binWidth = width;
        _binHeight = height;
        _allowRotation = allowRotation;
        _method = method;
        _freeRectangles.Add(new PackerRect(0, 0, width, height));
    }

    public PackerRect BinSize => new(0, 0, _binWidth, _binHeight);

    /// <summary>
    /// 尝试把 w x h 的精灵放入图集。放不下时返回 Width==0&&Height==0 的空矩形。
    /// </summary>
    public PackerRect Insert(int width, int height, out bool rotated)
    {
        rotated = false;
        PackerRect newNode = ScoreRect(width, height, out float score1, out float score2);
        if (newNode.Width == 0 && newNode.Height == 0)
        {
            if (_allowRotation && width != height)
            {
                newNode = ScoreRect(height, width, out score1, out score2);
                if (newNode.Width != 0 || newNode.Height != 0)
                    rotated = true;
            }
        }

        if (newNode.Width == 0 && newNode.Height == 0)
            return newNode; // 放不下

        PlaceRect(newNode);
        return newNode;
    }

    private PackerRect ScoreRect(int width, int height, out float score1, out float score2)
    {
        PackerRect bestNode = default;
        score1 = float.MaxValue;
        score2 = float.MaxValue;
        switch (_method)
        {
            case MaxRectsMethod.BestShortSideFit:
                bestNode = FindPositionForNewNodeBestShortSideFit(width, height, out score1, out score2);
                break;
            case MaxRectsMethod.BestLongSideFit:
                bestNode = FindPositionForNewNodeBestLongSideFit(width, height, out score1, out score2);
                break;
            case MaxRectsMethod.BottomLeftRule:
                bestNode = FindPositionForNewNodeBottomLeft(width, height, out score1, out score2);
                break;
            case MaxRectsMethod.ContactPointRule:
                bestNode = FindPositionForNewNodeContactPoint(width, height, out score1, out score2);
                break;
        }

        if (bestNode.Width == 0 && bestNode.Height == 0)
        {
            score1 = float.MaxValue;
            score2 = float.MaxValue;
        }

        return bestNode;
    }

    private PackerRect FindPositionForNewNodeBestShortSideFit(int width, int height, out float bestShortSideFit, out float bestLongSideFit)
    {
        PackerRect bestNode = default;
        bestShortSideFit = float.MaxValue;
        bestLongSideFit = float.MaxValue;
        for (int i = 0; i < _freeRectangles.Count; i++)
        {
            if (_freeRectangles[i].Width >= width && _freeRectangles[i].Height >= height)
            {
                int leftoverHoriz = Math.Abs(_freeRectangles[i].Width - width);
                int leftoverVert = Math.Abs(_freeRectangles[i].Height - height);
                int shortSideFit = Math.Min(leftoverHoriz, leftoverVert);
                int longSideFit = Math.Max(leftoverHoriz, leftoverVert);
                if (shortSideFit < bestShortSideFit ||
                    (shortSideFit == bestShortSideFit && longSideFit < bestLongSideFit))
                {
                    bestNode = new PackerRect(_freeRectangles[i].X, _freeRectangles[i].Y, width, height);
                    bestShortSideFit = shortSideFit;
                    bestLongSideFit = longSideFit;
                }
            }
        }

        return bestNode;
    }

    private PackerRect FindPositionForNewNodeBestLongSideFit(int width, int height, out float bestShortSideFit, out float bestLongSideFit)
    {
        PackerRect bestNode = default;
        bestShortSideFit = float.MaxValue;
        bestLongSideFit = float.MaxValue;
        for (int i = 0; i < _freeRectangles.Count; i++)
        {
            if (_freeRectangles[i].Width >= width && _freeRectangles[i].Height >= height)
            {
                int leftoverHoriz = Math.Abs(_freeRectangles[i].Width - width);
                int leftoverVert = Math.Abs(_freeRectangles[i].Height - height);
                int shortSideFit = Math.Min(leftoverHoriz, leftoverVert);
                int longSideFit = Math.Max(leftoverHoriz, leftoverVert);
                if (longSideFit < bestLongSideFit ||
                    (longSideFit == bestLongSideFit && shortSideFit < bestShortSideFit))
                {
                    bestNode = new PackerRect(_freeRectangles[i].X, _freeRectangles[i].Y, width, height);
                    bestShortSideFit = shortSideFit;
                    bestLongSideFit = longSideFit;
                }
            }
        }

        return bestNode;
    }

    private PackerRect FindPositionForNewNodeBottomLeft(int width, int height, out float bestY, out float bestX)
    {
        PackerRect bestNode = default;
        bestY = float.MaxValue;
        bestX = float.MaxValue;
        for (int i = 0; i < _freeRectangles.Count; i++)
        {
            if (_freeRectangles[i].Width >= width && _freeRectangles[i].Height >= height)
            {
                int topSideY = _freeRectangles[i].Y + height;
                if (topSideY < bestY || (topSideY == bestY && _freeRectangles[i].X < bestX))
                {
                    bestNode = new PackerRect(_freeRectangles[i].X, _freeRectangles[i].Y, width, height);
                    bestY = topSideY;
                    bestX = _freeRectangles[i].X;
                }
            }
        }

        return bestNode;
    }

    private PackerRect FindPositionForNewNodeContactPoint(int width, int height, out float bestContactScore, out float _)
    {
        PackerRect bestNode = default;
        bestContactScore = float.MaxValue;
        _ = 0;
        for (int i = 0; i < _freeRectangles.Count; i++)
        {
            if (_freeRectangles[i].Width >= width && _freeRectangles[i].Height >= height)
            {
                var node = new PackerRect(_freeRectangles[i].X, _freeRectangles[i].Y, width, height);
                int score = ContactPointScoreNode(node);
                if (score < bestContactScore)
                {
                    bestNode = node;
                    bestContactScore = score;
                }
            }
        }

        return bestNode;
    }

    private int ContactPointScoreNode(PackerRect node)
    {
        int score = 0;
        if (node.X == 0 || node.X + node.Width == _binWidth)
            score += node.Height;
        if (node.Y == 0 || node.Y + node.Height == _binHeight)
            score += node.Width;

        foreach (var used in _usedRectangles)
        {
            if (used.X == node.X + node.Width || used.X + used.Width == node.X)
                score += CommonIntervalLength(used.Y, used.Height, node.Y, node.Height);
            if (used.Y == node.Y + node.Height || used.Y + used.Height == node.Y)
                score += CommonIntervalLength(used.X, used.Width, node.X, node.Width);
        }

        return score;
    }

    private static int CommonIntervalLength(int i1, int len1, int i2, int len2)
    {
        int end1 = i1 + len1;
        int end2 = i2 + len2;
        if (i1 < i2)
        {
            if (i2 < end1)
                return Math.Min(end1, end2) - i2;
        }
        else if (i2 < i1)
        {
            if (i1 < end2)
                return Math.Min(end1, end2) - i1;
        }

        return 0;
    }

    private void PlaceRect(PackerRect node)
    {
        for (int i = _freeRectangles.Count - 1; i >= 0; i--)
        {
            if (SplitFreeNode(_freeRectangles[i], node, out var splits))
            {
                _freeRectangles.RemoveAt(i);
                _freeRectangles.AddRange(splits);
            }
        }

        PruneFreeList();
        _usedRectangles.Add(node);
    }

    private static bool SplitFreeNode(PackerRect freeNode, PackerRect usedNode, out List<PackerRect> splitNodes)
    {
        splitNodes = new List<PackerRect>();

        if (usedNode.X >= freeNode.X + freeNode.Width || usedNode.X + usedNode.Width <= freeNode.X ||
            usedNode.Y >= freeNode.Y + freeNode.Height || usedNode.Y + usedNode.Height <= freeNode.Y)
            return false;

        if (usedNode.X < freeNode.X + freeNode.Width && usedNode.X + usedNode.Width > freeNode.X &&
            usedNode.Y < freeNode.Y + freeNode.Height && usedNode.Y + usedNode.Height > freeNode.Y)
        {
            // 上
            if (usedNode.Y > freeNode.Y && usedNode.Y < freeNode.Y + freeNode.Height)
                splitNodes.Add(new PackerRect(freeNode.X, freeNode.Y, freeNode.Width, usedNode.Y - freeNode.Y));
            // 下
            if (usedNode.Y + usedNode.Height < freeNode.Y + freeNode.Height)
                splitNodes.Add(new PackerRect(freeNode.X, usedNode.Y + usedNode.Height, freeNode.Width,
                    freeNode.Y + freeNode.Height - (usedNode.Y + usedNode.Height)));
            // 左
            if (usedNode.X > freeNode.X && usedNode.X < freeNode.X + freeNode.Width)
                splitNodes.Add(new PackerRect(freeNode.X, freeNode.Y, usedNode.X - freeNode.X, freeNode.Height));
            // 右
            if (usedNode.X + usedNode.Width < freeNode.X + freeNode.Width)
                splitNodes.Add(new PackerRect(usedNode.X + usedNode.Width, freeNode.Y,
                    freeNode.X + freeNode.Width - (usedNode.X + usedNode.Width), freeNode.Height));
        }

        return true;
    }

    private void PruneFreeList()
    {
        for (int i = 0; i < _freeRectangles.Count; i++)
        {
            for (int j = i + 1; j < _freeRectangles.Count; j++)
            {
                if (IsContainedIn(_freeRectangles[i], _freeRectangles[j]))
                {
                    _freeRectangles.RemoveAt(i);
                    i--;
                    break;
                }

                if (IsContainedIn(_freeRectangles[j], _freeRectangles[i]))
                {
                    _freeRectangles.RemoveAt(j);
                    j--;
                }
            }
        }
    }

    private static bool IsContainedIn(PackerRect a, PackerRect b)
        => a.X >= b.X && a.Y >= b.Y &&
           a.X + a.Width <= b.X + b.Width &&
           a.Y + a.Height <= b.Y + b.Height;
}

public enum MaxRectsMethod
{
    BestShortSideFit,
    BestLongSideFit,
    BottomLeftRule,
    ContactPointRule,
}
