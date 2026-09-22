using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 血量 / 计数器组件（M5.5 P4）：桌面上一个可拖动的"数值牌"，显示形如 <c>血量 60/60</c>。
///
/// <b>为什么做成第四种物件（用户拍板）：</b>血量是<b>对局中会变的状态</b>，
/// 而卡牌字段是<b>卡的定义</b> —— 前者要能独立于任何一张卡存在。
/// 详见 <see cref="ObjectKind.Stat"/> 的说明。
///
/// <b>改数值必须进历史</b>：数值住在 <see cref="ObjectState.StatCurrent"/> 等字段里，
/// 且 <c>SameAs</c> 会比它们 —— 那条是骰子点数当年栽过的坑（"改了却撤不掉"），
/// 同一个坑不能再踩第二次。
/// </summary>
public partial class StatObject : TabletopObject
{
	public override ObjectKind Kind => ObjectKind.Stat;

	/// <summary>数据（标签 / 当前值 / 上限）。就地改它之后记得 <see cref="NotifyChanged"/>。</summary>
	public StatData Data { get; private set; } = new();

	/// <summary>计数器要好点中 —— 它比卡牌小。</summary>
	protected override float HitPadding => 4f;

	public void SetData(StatData data)
	{
		Data = data;
		Size = GameConfig.StatSize;
		QueueRedraw();
	}

	public void SetLabel(string label)
	{
		Data.Label = label;
		QueueRedraw();
	}

	/// <summary>
	/// 改当前值。<b>返回是否真的变了</b> —— 调用方（物件系统）靠它决定要不要记历史，
	/// 于是"加 0"这种没有净变化的操作不会往历史里塞一条空记录。
	/// </summary>
	public bool AddCurrent(int delta)
	{
		if (delta == 0)
			return false;

		Data.Current += delta;
		QueueRedraw();
		return true;
	}

	/// <summary>改上限。同样是"没变就不记"。</summary>
	public bool SetMax(int max)
	{
		if (max == Data.Max)
			return false;

		Data.Max = max;
		QueueRedraw();
		return true;
	}

	public void NotifyChanged() => QueueRedraw();

	/// <summary>是否超过上限（画面上给一条额外提示）。</summary>
	public bool IsOverMax => Data.Max > 0 && Data.Current > Data.Max;

	protected override void DrawContent()
	{
		Rect2 rect = LocalRect;

		// 底板：圆角矩形 + 描边，与卡背同一种质感
		var box = new StyleBoxFlat
		{
			BgColor = GameConfig.StatBackground,
			CornerRadiusTopLeft = 10,
			CornerRadiusTopRight = 10,
			CornerRadiusBottomLeft = 10,
			CornerRadiusBottomRight = 10,
			BorderWidthLeft = 3,
			BorderWidthRight = 3,
			BorderWidthTop = 3,
			BorderWidthBottom = 3,
			BorderColor = IsOverMax ? new Color("#b48ead") : GameConfig.StatBorder,
		};

		DrawStyleBox(box, rect);

		Font font = Fonts.Ui;
		int valueSize = GameConfig.StatValueFontSize;
		int labelSize = GameConfig.StatLabelFontSize;

		// 数值居中。DrawString 的 pos 是<b>基线</b>不是左上角（M2 踩过），
		// 所以纵向居中要自己减掉 ascent/descent 的一半。
		string value = $"{Data.Current}/{Data.Max}";
		Vector2 valueMeasure = font.GetStringSize(value, HorizontalAlignment.Left, -1f, valueSize);
		float ascent = font.GetAscent(valueSize);
		float descent = font.GetDescent(valueSize);

		var valuePos = new Vector2(
			rect.GetCenter().X - (valueMeasure.X * 0.5f),
			rect.GetCenter().Y + ((ascent - descent) * 0.5f));

		DrawString(font, valuePos, value, HorizontalAlignment.Left, -1f, valueSize, Data.ValueColor);

		// 标签画在数值上方一行（没有标签就整块留白，不画）
		if (!string.IsNullOrWhiteSpace(Data.Label))
		{
			Vector2 labelMeasure = font.GetStringSize(Data.Label, HorizontalAlignment.Left, -1f, labelSize);
			var labelPos = new Vector2(
				rect.GetCenter().X - (labelMeasure.X * 0.5f),
				rect.Position.Y + GameConfig.StatPadding + font.GetAscent(labelSize));

			DrawString(font, labelPos, Data.Label, HorizontalAlignment.Left, -1f, labelSize, GameConfig.StatLabelColor);
		}
	}

	protected override void CaptureExtra(ObjectState state)
	{
		state.StatCurrent = Data.Current;
		state.StatMax = Data.Max;
		state.StatLabel = Data.Label;
	}

	protected override void ApplyExtra(ObjectState state)
	{
		Data.Current = state.StatCurrent;
		Data.Max = state.StatMax;
		Data.Label = state.StatLabel;
		QueueRedraw();
	}
}
