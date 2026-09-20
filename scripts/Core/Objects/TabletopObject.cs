using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 桌面物件的抽象基类。
///
/// 分工：<b>基类管交互状态</b>（选中 / 悬停 / 盖放 / 堆叠 / 命中测试 / 状态快照），
/// <b>子类只管画自己的内容</b>（<see cref="DrawContent"/>）。
/// 于是卡牌、Token、骰子的选中描边、层叠徽章、状态存取全都是同一套代码。
///
/// 坐标约定：本地原点在物件<b>中心</b>，因此旋转天然绕中心转，不需要额外配轴心。
/// </summary>
public abstract partial class TabletopObject : Node2D
{
	/// <summary>描边用的复用样式盒（圆形物件会覆写 <see cref="DrawOutlineShape"/>）。</summary>
	private static readonly StyleBoxFlat OutlineBox = new();

	/// <summary>稳定唯一 id。<b>绝不复用</b> —— 存档、堆成员、撤销记录都靠它。</summary>
	public string Uid { get; private set; } = "";

	public abstract ObjectKind Kind { get; }

	/// <summary>物件尺寸（本地坐标，未旋转）。</summary>
	public Vector2 Size { get; set; } = GameConfig.DefaultCardSize;

	/// <summary>是否盖放（卡牌显示卡背）。</summary>
	public bool IsFaceDown { get; set; }

	// ---- 堆叠（由 ObjectManager 维护，物件自己不算）----
	public int PileId { get; set; }
	public int PileIndex { get; set; }
	public int PileCount { get; set; }

	// ---- 交互状态 ----
	public bool IsSelected { get; set; }
	public bool IsHovered { get; set; }

	/// <summary>本地矩形，原点在中心。</summary>
	public Rect2 LocalRect => new(-Size * 0.5f, Size);

	/// <summary>旋转角（度）。Godot 内部用弧度，但配置与存档都用度更直观。</summary>
	public float RotationDeg
	{
		get => Mathf.RadToDeg(Rotation);
		set => Rotation = Mathf.DegToRad(value);
	}

	/// <summary>命中测试额外放大的边距。缩得很小的物件需要它，否则点不中。</summary>
	protected virtual float HitPadding => 0f;

	/// <summary>描边圆角半径。方形物件用它贴近卡面圆角，圆形物件不用。</summary>
	protected virtual float OutlineCornerRadius => 0f;

	// ------------------------------------------------------------------ 生命周期

	public void AssignUid(string uid) => Uid = uid;

	/// <summary>所有物件都通过这个入口自绘。子类不要覆写它，覆写 <see cref="DrawContent"/>。</summary>
	public override void _Draw()
	{
		DrawContent();

		if (IsSelected)
			DrawOutline(GameConfig.SelectionOutline, 5f);
		else if (IsHovered)
			DrawOutline(GameConfig.HoverOutline, 3f);

		DrawPileBadge();
	}

	/// <summary>子类在这里画自己的内容。基类不干涉。</summary>
	protected abstract void DrawContent();

	// ------------------------------------------------------------------ 命中与包围盒

	/// <summary>
	/// 世界坐标是否落在物件内。
	/// <see cref="Node2D.ToLocal"/> 会逆转旋转，所以旋转后的卡也点得准，无需自己做矩阵。
	/// </summary>
	public virtual bool ContainsWorldPoint(Vector2 world)
		=> LocalRect.Grow(HitPadding).HasPoint(ToLocal(world));

	/// <summary>旋转后的世界轴对齐包围盒（框选与视口裁剪用）。</summary>
	public Rect2 GetWorldAabb()
	{
		Vector2 half = Size * 0.5f;
		Vector2 p0 = ToGlobal(new Vector2(-half.X, -half.Y));
		Vector2 p1 = ToGlobal(new Vector2(half.X, -half.Y));
		Vector2 p2 = ToGlobal(new Vector2(half.X, half.Y));
		Vector2 p3 = ToGlobal(new Vector2(-half.X, half.Y));

		Vector2 min = p0.Min(p1).Min(p2).Min(p3);
		Vector2 max = p0.Max(p1).Max(p2).Max(p3);
		return new Rect2(min, max - min);
	}

	// ------------------------------------------------------------------ 状态快照

	/// <summary>把当前状态抓成可序列化快照（M4 存档 + 撤销都用它）。</summary>
	public ObjectState CaptureState()
	{
		ObjectState state = new()
		{
			Uid = Uid,
			Kind = Kind,
			Position = Position,
			RotationDegrees = RotationDeg,
			FaceDown = IsFaceDown,
			PileId = PileId,
			PileIndex = PileIndex,
		};

		CaptureExtra(state);
		return state;
	}

	/// <summary>写回快照。注意会重建子类内部状态，不只是位置。</summary>
	public void ApplyState(ObjectState state)
	{
		Position = state.Position;
		RotationDeg = state.RotationDegrees;
		IsFaceDown = state.FaceDown;
		PileId = state.PileId;
		PileIndex = state.PileIndex;

		ApplyExtra(state);
		QueueRedraw();
	}

	/// <summary>子类补充自己的字段进快照。</summary>
	protected virtual void CaptureExtra(ObjectState state) { }

	/// <summary>子类从快照恢复自己的字段。</summary>
	protected virtual void ApplyExtra(ObjectState state) { }

	// ------------------------------------------------------------------ 绘制工具

	/// <summary>画描边。方形物件会走 <see cref="DrawOutlineShape"/> 以贴合圆角。</summary>
	protected void DrawOutline(Color color, float width)
	{
		DrawOutlineShape(LocalRect.Grow(width * 0.5f), color, width);
	}

	/// <summary>子类可覆写成非矩形描边（例如圆形 Token 用圆环）。</summary>
	protected virtual void DrawOutlineShape(Rect2 rect, Color color, float width)
	{
		OutlineBox.BgColor = Colors.Transparent;
		OutlineBox.BorderColor = color;
		OutlineBox.DrawCenter = false;
		OutlineBox.ShadowSize = 0;
		OutlineBox.ShadowOffset = Vector2.Zero;
		OutlineBox.SetBorderWidthAll(Mathf.RoundToInt(width));
		OutlineBox.SetCornerRadiusAll(Mathf.RoundToInt(OutlineCornerRadius + width));
		DrawStyleBox(OutlineBox, rect);
	}

	/// <summary>堆叠数量徽章 —— 只在堆顶那张上画。</summary>
	private void DrawPileBadge()
	{
		if (PileCount < 2 || PileIndex != PileCount - 1)
			return;

		float radius = Mathf.Clamp(Size.X * 0.11f, 11f, 30f);
		Vector2 center = new(LocalRect.End.X - (radius * 0.3f), LocalRect.Position.Y + (radius * 0.3f));

		DrawCircle(center, radius, GameConfig.PileBadgeFill);
		DrawArc(center, radius, 0f, Mathf.Tau, 40, GameConfig.PileBadgeBorder, 2f, true);

		string text = PileCount.ToString();
		Font font = Fonts.Ui;
		int fontSize = Mathf.RoundToInt(radius * 1.2f);
		Vector2 textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize);
		float ascent = font.GetAscent(fontSize);
		float descent = font.GetDescent(fontSize);

		// 基线定位：让文字的视觉中心落在圆心
		Vector2 pos = center + new Vector2(-textSize.X * 0.5f, (ascent - descent) * 0.5f);
		DrawString(font, pos, text, HorizontalAlignment.Left, -1f, fontSize, GameConfig.PileBadgeText);
	}
}
