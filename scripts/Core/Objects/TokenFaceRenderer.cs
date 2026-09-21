using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 把 <see cref="TokenDefinition"/> 画成一个 Token。无状态，纯函数式绘制。
///
/// <b>存在的理由是"M5 的编辑器预览不能另写一份画法"</b>：
/// 卡面从一开始就是静态纯函数（<see cref="CardFaceRenderer"/>），
/// 所以卡牌预览只需要换一个 <c>CanvasItem</c> 再画一遍，两边不可能长得不一样。
/// Token 原来把画法写在 <c>TokenObject.DrawContent</c> 里，于是编辑器要预览
/// 就只有两条路：抄一份（迟早分叉），或者把画法抽出来。这里选了后者。
///
/// 抽出来之后「预览好看、桌上不一样」这类最让人不信任编辑器的症状
/// 在 Token 上也同样不可能发生。
/// </summary>
internal static class TokenFaceRenderer
{
	/// <summary>方形 Token 的圆角（与描边配套）。</summary>
	internal const float SquareCornerRadius = 10f;

	/// <summary>复用的样式盒。<c>_Draw</c> 都在主线程顺序执行，共享实例是安全的。</summary>
	private static readonly StyleBoxFlat Box = new();

	/// <summary>
	/// 画一个 Token。<paramref name="rect"/> 是以<b>中心为原点</b>的本地矩形
	/// （与 <c>TabletopObject.LocalRect</c> 同一个约定）。
	/// </summary>
	public static void Draw(CanvasItem target, TokenDefinition def, Rect2 rect, string textOverride = "")
	{
		Vector2 center = rect.GetCenter();
		float radius = rect.Size.X * 0.5f;

		switch (def.Shape)
		{
			case TokenShape.Circle:
				target.DrawCircle(center, radius, def.Fill);
				target.DrawArc(center, radius, 0f, Mathf.Tau, 64, def.Border, def.BorderWidth, true);
				break;

			case TokenShape.Square:
				DrawBox(target, rect, def.Fill, def.Border, def.BorderWidth, SquareCornerRadius);
				break;

			case TokenShape.Hexagon:
				DrawPolygon(target, RegularPolygon(center, radius, 6, -Mathf.Pi / 2f), def);
				break;

			case TokenShape.Triangle:
				DrawPolygon(target, RegularPolygon(center, radius, 3, -Mathf.Pi / 2f), def);
				break;
		}

		DrawInnerContent(target, def, center, radius, textOverride);
	}

	/// <summary>形状的描边（编辑器预览与物件描边共用同一个形状判断）。</summary>
	public static void DrawOutline(CanvasItem target, TokenDefinition def, Rect2 rect, Color color, float width)
	{
		if (def.Shape == TokenShape.Circle)
		{
			float r = (rect.Size.X * 0.5f) + (width * 0.5f);
			target.DrawArc(rect.GetCenter(), r, 0f, Mathf.Tau, 64, color, width, true);
			return;
		}

		DrawBox(target, rect, Colors.Transparent, color, width,
			def.Shape == TokenShape.Square ? SquareCornerRadius : 0f);
	}

	private static void DrawInnerContent(
		CanvasItem target, TokenDefinition def, Vector2 center, float radius, string textOverride)
	{
		Texture2D? image = TextureStore.Get(def.Image);

		if (image is not null)
		{
			float side = radius * 1.15f;
			target.DrawTextureRect(image, new Rect2(center - (Vector2.One * side * 0.5f), Vector2.One * side), false);
		}

		string text = string.IsNullOrEmpty(textOverride) ? def.Text : textOverride;
		if (string.IsNullOrEmpty(text))
			return;

		Font font = Fonts.Ui;
		Vector2 textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1f, def.FontSize);
		float ascent = font.GetAscent(def.FontSize);
		float descent = font.GetDescent(def.FontSize);
		Vector2 pos = center
			+ new Vector2(-textSize.X * 0.5f, (ascent - descent) * 0.5f)
			+ new Vector2(0f, def.TextOffsetY);

		target.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, def.FontSize, def.TextColor);
	}

	/// <summary>多边形 Token：填充 + 闭合描边。</summary>
	private static void DrawPolygon(CanvasItem target, Vector2[] points, TokenDefinition def)
	{
		target.DrawColoredPolygon(points, def.Fill);

		// 描边要把首点补回末尾才闭合
		var closed = new Vector2[points.Length + 1];
		points.CopyTo(closed, 0);
		closed[^1] = points[0];
		target.DrawPolyline(closed, def.Border, def.BorderWidth, true);
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

	private static void DrawBox(
		CanvasItem target, Rect2 rect, Color fill, Color border, float borderWidth, float radius)
	{
		Box.BgColor = fill;
		Box.BorderColor = border;
		Box.DrawCenter = fill.A > 0f;
		Box.ShadowSize = 0;
		Box.ShadowOffset = Vector2.Zero;
		Box.SetBorderWidthAll(Mathf.RoundToInt(borderWidth));
		Box.SetCornerRadiusAll(Mathf.RoundToInt(radius));
		target.DrawStyleBox(Box, rect);
	}
}
