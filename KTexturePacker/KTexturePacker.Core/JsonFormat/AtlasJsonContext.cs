using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// System.Text.Json 源生成上下文：把 JsonFormat 目录下所有强类型模型纳入编译期代码生成，
/// 避免 KTexturePacker.Web（<c>PublishAot=true</c>）出现 IL2026 / IL3050 反射裁剪警告。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AtlasData))]
[JsonSerializable(typeof(AtlasPageData))]
[JsonSerializable(typeof(AtlasRegionData))]
[JsonSerializable(typeof(PixiAtlasSheet))]
[JsonSerializable(typeof(PixiFrameData))]
[JsonSerializable(typeof(PixiMetaData))]
[JsonSerializable(typeof(PixiRectData))]
[JsonSerializable(typeof(PixiSizeData))]
[JsonSerializable(typeof(Dictionary<string, PixiFrameData>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<string>))]
public partial class AtlasJsonContext : JsonSerializerContext;
