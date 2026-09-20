using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Data;

/// <summary>
/// 卡面布局槽位。
///
/// 用<b>固定槽位</b>而不是自由坐标，是因为：手摆坐标做出来的卡十张有九张歪，
/// 而玩法验证阶段你真正在意的是「这行数字对不对」，不是「这个数字偏了 3 像素」。
/// 需要换版式时换 <see cref="CardFaceTemplate"/> 的整套参数，而不是逐张卡调坐标。
/// </summary>
public enum FieldSlot
{
	/// <summary>卡名。</summary>
	Title,
	/// <summary>标题下方的一条（种族 / 类型 / 标签）。</summary>
	TypeLine,

	TopLeft,
	TopRight,

	CenterLeft,
	Center,
	CenterRight,

	BottomLeft,
	BottomCenter,
	BottomRight,

	/// <summary>下半部分，自动换行。</summary>
	Description,
}

/// <summary>卡面上的一个自定义字段。字段集合是数据、不是代码 —— 玩法验证时它本身就在变。</summary>
public sealed class CardField
{
	/// <summary>字段键，例如 "cost"。同名键可用于实例级覆盖。</summary>
	public string Key { get; set; } = "";

	/// <summary>显示用的标签，例如 "费用"。留空则直接显示 <see cref="Key"/>。</summary>
	public string Label { get; set; } = "";

	public string Value { get; set; } = "";

	public FieldSlot Slot { get; set; } = FieldSlot.Center;

	/// <summary>字号；0 = 用模板默认值。</summary>
	public int FontSize { get; set; }

	/// <summary>颜色；null = 用模板默认值。</summary>
	public Color? Color { get; set; }

	/// <summary>是否连标签一起显示（"费用 3" 而非 "3"）。</summary>
	public bool ShowLabel { get; set; }

	/// <summary>拼出最终显示的文本。</summary>
	public string Render()
	{
		if (!ShowLabel)
			return Value;

		string label = string.IsNullOrEmpty(Label) ? Key : Label;
		return string.IsNullOrEmpty(label) ? Value : $"{label} {Value}";
	}

	public CardField Clone() => new()
	{
		Key = Key,
		Label = Label,
		Value = Value,
		Slot = Slot,
		FontSize = FontSize,
		Color = Color,
		ShowLabel = ShowLabel,
	};
}

/// <summary>
/// 卡面版式参数。整套可换 —— 这就是「自定义卡牌风格」的最小实现。
/// 所有相对量都按卡宽的比例给，于是换卡牌尺寸时版式自动等比缩放。
/// </summary>
public sealed class CardFaceTemplate
{
	/// <summary>内容内边距，相对卡宽。</summary>
	public float Padding { get; set; } = 0.06f;

	public int TitleFontSize { get; set; } = 34;
	public int FieldFontSize { get; set; } = 26;
	public int DescriptionFontSize { get; set; } = 20;

	public float CornerRadius { get; set; } = 14f;
	public float BorderWidth { get; set; } = 3f;

	public Color TitleColor { get; set; } = new("#eceff4");
	public Color FieldColor { get; set; } = new("#d8dee9");
	public Color DescriptionColor { get; set; } = new("#c8ced9");

	/// <summary>投影偏移（像素）。设为零向量即关闭投影。</summary>
	public Vector2 ShadowOffset { get; set; } = new(0f, 5f);
	public int ShadowSize { get; set; } = 6;
	public Color ShadowColor { get; set; } = new(0f, 0f, 0f, 0.35f);

	/// <summary>
	/// 没有定义 <see cref="FieldSlot.Title"/> 字段时，是否用 <see cref="CardDefinition.DisplayName"/>
	/// 顶替标题。默认开 —— 这样只写卡名的占位卡也能正常显示，不必配字段。
	/// </summary>
	public bool UseDisplayNameAsTitle { get; set; } = true;

	public CardFaceTemplate Clone() => new()
	{
		Padding = Padding,
		TitleFontSize = TitleFontSize,
		FieldFontSize = FieldFontSize,
		DescriptionFontSize = DescriptionFontSize,
		CornerRadius = CornerRadius,
		BorderWidth = BorderWidth,
		TitleColor = TitleColor,
		FieldColor = FieldColor,
		DescriptionColor = DescriptionColor,
		ShadowOffset = ShadowOffset,
		ShadowSize = ShadowSize,
		ShadowColor = ShadowColor,
		UseDisplayNameAsTitle = UseDisplayNameAsTitle,
	};
}

/// <summary>
/// 卡牌定义 —— 模板合成里的「模板」。
/// 一张实体卡 = 这份定义 + 一组实例级字段覆盖。
/// </summary>
public sealed class CardDefinition
{
	/// <summary>存档内唯一 id。存档与卡组都靠它引用。</summary>
	public string Id { get; set; } = "";

	public string DisplayName { get; set; } = "";

	/// <summary>卡面底图文件名（位于存档的 images/ 目录下）。空 = 纯色卡面。</summary>
	public string FaceImage { get; set; } = "";

	/// <summary>卡背图文件名。空 = 程序化生成的默认卡背。</summary>
	public string BackImage { get; set; } = "";

	public Color FaceTint { get; set; } = new("#3d4b63");
	public Color BackTint { get; set; } = new("#2b3242");
	public Color BorderColor { get; set; } = new("#8fbcbb");

	public CardFaceTemplate Template { get; set; } = new();

	public List<CardField> Fields { get; set; } = new();

	/// <summary>按字段键取值，实例覆盖优先。</summary>
	public string GetFieldValue(string key, IReadOnlyDictionary<string, string>? overrides = null)
	{
		if (overrides is not null && overrides.TryGetValue(key, out string? overridden))
			return overridden;

		foreach (CardField f in Fields)
		{
			if (f.Key == key)
				return f.Value;
		}

		return "";
	}

	/// <summary>把一个字段的值解析成整数（解析失败返回 0）。改数值做平衡时用。</summary>
	public int GetFieldInt(string key, IReadOnlyDictionary<string, string>? overrides = null)
		=> int.TryParse(GetFieldValue(key, overrides), out int v) ? v : 0;

	public CardDefinition Clone() => new()
	{
		Id = Id,
		DisplayName = DisplayName,
		FaceImage = FaceImage,
		BackImage = BackImage,
		FaceTint = FaceTint,
		BackTint = BackTint,
		BorderColor = BorderColor,
		Template = Template.Clone(),
		Fields = CloneFields(),
	};

	private List<CardField> CloneFields()
	{
		var list = new List<CardField>(Fields.Count);
		foreach (CardField f in Fields)
			list.Add(f.Clone());
		return list;
	}
}
