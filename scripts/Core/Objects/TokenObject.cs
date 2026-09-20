using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// Token —— 不翻面的小标记：伤害指示物、资源、状态标记。
/// 与卡牌的区别：没有卡背、没有字段列表，内容就是一段文字或一张图。
/// </summary>
public partial class TokenObject : TabletopObject
{
	/// <summary>描边的圆角，方形 Token 用它贴合形状。</summary>
	private const float SquareCornerRadius = 10f;

	public override ObjectKind Kind => ObjectKind.Token;

	public TokenDefinition Definition { get; private set; } = new();

	/// <summary>实例级文字覆盖（同一份定义打出多个不同数值的指示物）。</summary>
	public string TextOverride { get; set; } = "";

	/// <summary>小 Token 也要点得中。</summary>
	protected override float HitPadding => 6f;

	protected override float OutlineCornerRadius =>
		Definition.Shape == TokenShape.Square ? SquareCornerRadius : 0f;

	public void SetDefinition(TokenDefinition definition)
	{
		Definition = definition;
		Size = new Vector2(definition.Size, definition.Size);
		QueueRedraw();
	}

	/// <summary>当前显示的文本（覆盖优先）。</summary>
	public string CurrentText => string.IsNullOrEmpty(TextOverride) ? Definition.Text : TextOverride;

	protected override void DrawContent()
	{
		Vector2 center = Vector2.Zero;
		float radius = Size.X * 0.5f;
		TokenDefinition def = Definition;

		switch (def.Shape)
		{
			case TokenShape.Circle:
				DrawCircle(center, radius, def.Fill);
				DrawArc(center, radius, 0f, Mathf.Tau, 64, def.Border, def.BorderWidth, true);
				break;

			case TokenShape.Square:
				DrawFilledBox(LocalRect, def.Fill, def.Border, def.BorderWidth, SquareCornerRadius);
				break;

			case TokenShape.Hexagon:
				DrawPolygonShape(RegularPolygon(center, radius, 6, -Mathf.Pi / 2f), def);
				break;

			case TokenShape.Triangle:
				DrawPolygonShape(RegularPolygon(center, radius, 3, -Mathf.Pi / 2f), def);
				break;
		}

		DrawInnerContent(center, radius);
	}

	private void DrawInnerContent(Vector2 center, float radius)
	{
		TokenDefinition def = Definition;
		Texture2D? image = TextureStore.Get(def.Image);

		if (image is not null)
		{
			float side = radius * 1.15f;
			DrawTextureRect(image, new Rect2(center - (Vector2.One * side * 0.5f), Vector2.One * side), false);
		}

		string text = CurrentText;
		if (string.IsNullOrEmpty(text))
			return;

		Font font = Fonts.Ui;
		Vector2 textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1f, def.FontSize);
		float ascent = font.GetAscent(def.FontSize);
		float descent = font.GetDescent(def.FontSize);
		Vector2 pos = center
			+ new Vector2(-textSize.X * 0.5f, (ascent - descent) * 0.5f)
			+ new Vector2(0f, def.TextOffsetY);

		DrawString(font, pos, text, HorizontalAlignment.Left, -1f, def.FontSize, def.TextColor);
	}

	/// <summary>多边形 Token：填充 + 闭合描边。</summary>
	private void DrawPolygonShape(Vector2[] points, TokenDefinition def)
	{
		DrawColoredPolygon(points, def.Fill);

		// 描边要把首点补回末尾才闭合
		var closed = new Vector2[points.Length + 1];
		points.CopyTo(closed, 0);
		closed[^1] = points[0];
		DrawPolyline(closed, def.Border, def.BorderWidth, true);
	}

	private static Vector2[] RegularPolygon(Vector2 center, float radius, int sides, float startAngle)
	{
		var points = new Vector2[sides];
		for (int i = 0; i < sides; i++)
		{
			float angle = startAngle + (Mathf.Tau * i / sides);
			points[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
		}

		return points;
	}

	/// <summary>圆形 Token 的描边改成圆环，方形走基类的圆角矩形。</summary>
	protected override void DrawOutlineShape(Rect2 rect, Color color, float width)
	{
		if (Definition.Shape == TokenShape.Circle)
		{
			float r = (Size.X * 0.5f) + (width * 0.5f);
			DrawArc(Vector2.Zero, r, 0f, Mathf.Tau, 64, color, width, true);
			return;
		}

		base.DrawOutlineShape(rect, color, width);
	}

	private static readonly StyleBoxFlat Box = new();

	private void DrawFilledBox(Rect2 rect, Color fill, Color border, float borderWidth, float radius)
	{
		Box.BgColor = fill;
		Box.BorderColor = border;
		Box.DrawCenter = true;
		Box.ShadowSize = 0;
		Box.ShadowOffset = Vector2.Zero;
		Box.SetBorderWidthAll(Mathf.RoundToInt(borderWidth));
		Box.SetCornerRadiusAll(Mathf.RoundToInt(radius));
		DrawStyleBox(Box, rect);
	}

	protected override void CaptureExtra(ObjectState state)
	{
		state.DefinitionId = Definition.Id;
		state.TokenTextOverride = TextOverride;
	}

	protected override void ApplyExtra(ObjectState state)
	{
		TextOverride = state.TokenTextOverride;
	}
}
