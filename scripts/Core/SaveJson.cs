using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 存档用的 JSON 读写。<b>整个 M4 只有这一处直接碰 <c>System.Text.Json</c>。</b>
///
/// 为什么不用 Godot 自带的 <c>Json.Stringify</c>：它只认 <c>Variant</c> 能表示的东西，
/// 我们的定义类是纯 POCO（<see cref="BoardTheme"/> / <c>CardDefinition</c> /
/// <c>ZoneDefinition</c>），走它就得先手工摊平成字典 —— 而那份"摊平代码"
/// 与类的字段一一对应，加一个字段就要记得改两处。<b>漏掉的那一处不会报错，
/// 只会让存档静默少一个字段。</b>
///
/// 三条硬约定：
/// <list type="number">
/// <item><b>可读优先。</b>缩进、camelCase、枚举写名字、颜色写 <c>"#rrggbbaa"</c>、
/// 向量写 <c>[x, y]</c>。存档是要给人改和给 git diff 的，不是压缩包。</item>
/// <item><b>中文不转义。</b>默认编码器会把「牌库」写成 <c>\u724c\u5e93</c>，
/// 于是"能手改、能 diff"当场作废 —— 必须用
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>。
/// 名字里的 "Unsafe" 是指它不转义 HTML 敏感字符；我们写的是本地文件，不是网页输出。</item>
/// <item><b>失败不抛穿。</b><see cref="Deserialize{T}"/> 出错返回 <c>null</c> 并打警告。
/// 存档是用户手改过的东西，读到坏文件应该表现为"打不开 + 说清楚哪坏了"，
/// 而不是把异常扔进 Godot 的主循环。</item>
/// </list>
/// </summary>
internal static class SaveJson
{
	/// <summary>
	/// 序列化选项。<b>只建一次</b> —— <see cref="JsonSerializerOptions"/> 第一次使用时
	/// 会缓存类型元数据，每次 new 一份等于每次重新反射一遍。
	/// </summary>
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters =
		{
			// 枚举写成名字（"stack" 而不是 1）。
			//
			// 注意必须写全限定名：Godot 命名空间里<b>也有</b>一个同名的
			// JsonStringEnumConverter，而 `using Godot;` 会把它带进来遮蔽掉 BCL 那个。
			// 拿错的那个不会报错 —— 它默默不生效，于是枚举被写成数字。
			// 自检第一次跑就抓到了这一条：json_enum_is_named 曾经是红的。
			new System.Text.Json.Serialization.JsonStringEnumConverter(),
			new ColorJsonConverter(),
			new Vector2JsonConverter(),
			new Rect2JsonConverter(),
		},
	};

	internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

	internal static T? Deserialize<T>(string text)
	{
		try
		{
			return JsonSerializer.Deserialize<T>(text, Options);
		}
		catch (JsonException e)
		{
			// 行号与位置信息都在 JsonException 里，原样带出去 —— 手改坏了要能定位。
			GD.PushWarning($"[SaveJson] 解析 {typeof(T).Name} 失败：{e.Message}");
			return default;
		}
	}
}

/// <summary>
/// <see cref="Color"/> ↔ <c>"#rrggbbaa"</c>。
///
/// 两种写法都收：6 位哈希补全为不透明。**写出去的一律是 8 位**。
///
/// 写 8 位不是啰嗦，是因为 Godot 在解析带 4 通道的字符串时，
/// <c>new Color("#3b4252ff")</c> 拿到的是 <c>(0xff, 0x52, 0xff, 0xff)</c> 这种
/// 反直觉的结果（4 通道按 r,g,b,a 的<b>位置</b>取值）。所以解析侧自己按字节切，
/// 不把这个坑留给下一个读代码的人。
/// </summary>
internal sealed class ColorJsonConverter : JsonConverter<Color>
{
	public override Color Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
	{
		string s = reader.GetString() ?? "";
		return Parse(s);
	}

	public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(
			$"#{ToByte(value.R):x2}{ToByte(value.G):x2}{ToByte(value.B):x2}{ToByte(value.A):x2}");
	}

	/// <summary>解析 <c>"#rrggbb"</c> / <c>"#rrggbbaa"</c>（<c>#</c> 可省）。失败返回洋红，便于一眼看出。</summary>
	private static Color Parse(string raw)
	{
		string hex = raw.Trim().TrimStart('#');

		if (hex.Length is not (6 or 8))
		{
			GD.PushWarning($"[SaveJson] 颜色写法不认识：\"{raw}\"（要 #rrggbb 或 #rrggbbaa）");
			return Colors.Magenta;
		}

		int r = Byte(hex, 0);
		int g = Byte(hex, 2);
		int b = Byte(hex, 4);
		int a = hex.Length == 8 ? Byte(hex, 6) : 255;
		return Color.Color8((byte)r, (byte)g, (byte)b, (byte)a);
	}

	/// <summary>十六进制取两位。非法字符返回 0xff，于是错误颜色明显偏亮，不会静默成黑色。</summary>
	private static int Byte(string hex, int offset)
		=> int.TryParse(hex.Substring(offset, 2), System.Globalization.NumberStyles.HexNumber,
			System.Globalization.CultureInfo.InvariantCulture, out int v)
			? v
			: 0xff;

	private static int ToByte(float channel)
		=> Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);

	/// <summary>
	/// 把颜色压到 8 位精度 —— 也就是存档里<b>真正能表示</b>的精度。
	///
	/// 存在的原因是它逼出了一条真 bug：<c>new Color(1, 1, 1, 0.11f)</c> 的
	/// <c>A</c> 是 <c>0.11f</c>（≈0.1099999994），而 <c>0.11 * 255 = 28.05</c>
	/// 四舍五入成 28，写出去再读回来是 <c>28/255 ≈ 0.10980392</c>。
	/// 于是"往返一致"这条断言最初是<b>假红</b>的 —— 产品没错，是断言拿
	/// 一个 8 位存储去要求 float 级相等。
	///
	/// 凡是要判断"两个颜色在存档里是否相同"，都必须先过这里。
	/// </summary>
	internal static Color Quantize(Color c)
		=> Color.Color8((byte)ToByte(c.R), (byte)ToByte(c.G), (byte)ToByte(c.B), (byte)ToByte(c.A));

	/// <summary>两个颜色在存档精度上是否相同（比的是量化后的 0–255 整数值）。</summary>
	internal static bool Matches(Color a, Color b)
		=> ToByte(a.R) == ToByte(b.R) && ToByte(a.G) == ToByte(b.G)
			&& ToByte(a.B) == ToByte(b.B) && ToByte(a.A) == ToByte(b.A);
}

/// <summary><see cref="Vector2"/> ↔ <c>[x, y]</c>。比 <c>{"x":..,"y":..}</c> 短，一屏能看完。</summary>
internal sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
	public override Vector2 Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartArray)
			throw new JsonException("Vector2 要写成 [x, y]");

		reader.Read();
		float x = reader.GetSingle();
		reader.Read();
		float y = reader.GetSingle();
		reader.Read();

		if (reader.TokenType != JsonTokenType.EndArray)
			throw new JsonException("Vector2 要写成 [x, y]（多了元素？）");

		return new Vector2(x, y);
	}

	public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();
		writer.WriteNumberValue(value.X);
		writer.WriteNumberValue(value.Y);
		writer.WriteEndArray();
	}
}

/// <summary><see cref="Rect2"/> ↔ <c>[x, y, 宽, 高]</c>。区域的矩形全靠它。</summary>
internal sealed class Rect2JsonConverter : JsonConverter<Rect2>
{
	public override Rect2 Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartArray)
			throw new JsonException("Rect2 要写成 [x, y, w, h]");

		reader.Read();
		float x = reader.GetSingle();
		reader.Read();
		float y = reader.GetSingle();
		reader.Read();
		float w = reader.GetSingle();
		reader.Read();
		float h = reader.GetSingle();
		reader.Read();

		if (reader.TokenType != JsonTokenType.EndArray)
			throw new JsonException("Rect2 要写成 [x, y, w, h]（多了元素？）");

		return new Rect2(x, y, w, h);
	}

	public override void Write(Utf8JsonWriter writer, Rect2 value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();
		writer.WriteNumberValue(value.Position.X);
		writer.WriteNumberValue(value.Position.Y);
		writer.WriteNumberValue(value.Size.X);
		writer.WriteNumberValue(value.Size.Y);
		writer.WriteEndArray();
	}
}
