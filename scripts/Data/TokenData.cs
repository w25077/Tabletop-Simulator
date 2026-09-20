using Godot;

namespace TabletopSimulator.Data;

/// <summary>Token 的形状。</summary>
public enum TokenShape
{
	Circle,
	Square,
	Hexagon,
	/// <summary>正三角（尖端朝上）。</summary>
	Triangle,
}

/// <summary>
/// Token 定义。Token 是「不翻面的小标记」—— 伤害指示物、资源、状态标记之类。
/// 与卡牌的区别：没有卡背、没有字段列表（内容就是一段文字或一张图）。
/// </summary>
public sealed class TokenDefinition
{
	public string Id { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public TokenShape Shape { get; set; } = TokenShape.Circle;

	/// <summary>外接尺寸（直径 / 边长）。</summary>
	public float Size { get; set; } = 160f;

	public Color Fill { get; set; } = new("#bf616a");
	public Color Border { get; set; } = new("#eceff4");
	public float BorderWidth { get; set; } = 4f;

	/// <summary>中心的图（images/ 下文件名）。空 = 不画图。</summary>
	public string Image { get; set; } = "";

	/// <summary>中心的文字。空 = 不画字。</summary>
	public string Text { get; set; } = "";

	public int FontSize { get; set; } = 44;
	public Color TextColor { get; set; } = new("#ffffff");

	/// <summary>文字在中心的下方偏移量（有图时把文字压到图下面）。</summary>
	public float TextOffsetY { get; set; }

	public TokenDefinition Clone() => new()
	{
		Id = Id,
		DisplayName = DisplayName,
		Shape = Shape,
		Size = Size,
		Fill = Fill,
		Border = Border,
		BorderWidth = BorderWidth,
		Image = Image,
		Text = Text,
		FontSize = FontSize,
		TextColor = TextColor,
		TextOffsetY = TextOffsetY,
	};
}
