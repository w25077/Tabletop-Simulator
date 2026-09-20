using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 把 <see cref="CardDefinition"/> 画成卡面。无状态，纯函数式绘制。
///
/// 为什么用 <c>_Draw()</c> 手绘而不是搭 Control 节点树：
/// 一张卡 = 1 个 Node2D。几百张卡时 Control 的 layout 开销会炸，
/// 而 Node2D 天然支持自由旋转缩放，和「像素级自由拖拽」的交互模型完全契合。
///
/// 为什么底板/边框/投影用 <see cref="StyleBoxFlat"/> 而不是 DrawRect：
/// DrawRect 画不出圆角，而 StyleBoxFlat 原生支持圆角、边框宽度、投影偏移。
/// 复用一个静态实例、改属性后重画，避免每张卡都分配新对象。
/// </summary>
internal static class CardFaceRenderer
{
	/// <summary>复用的样式盒。_Draw 都在主线程顺序执行，共享实例是安全的。</summary>
	private static readonly StyleBoxFlat Box = new();

	/// <summary>
	/// CJK 换行需要逐字可断（GraphemeBound）；纯 WordBound 会把一整句中文当成一个词，不换行。
	/// 注意 C# 绑定剥掉了枚举成员的公共前缀，是 WordBound / GraphemeBound，不是 WordBoundary。
	/// </summary>
	private const TextServer.LineBreakFlag BreakFlags =
		TextServer.LineBreakFlag.Mandatory |
		TextServer.LineBreakFlag.WordBound |
		TextServer.LineBreakFlag.GraphemeBound;

	// ------------------------------------------------------------------ 正面

	/// <param name="rect">卡面矩形，位于调用者的本地坐标系（通常以卡中心为原点）。</param>
	public static void DrawFace(
		CanvasItem target,
		CardDefinition def,
		Rect2 rect,
		IReadOnlyDictionary<string, string>? overrides = null)
	{
		CardFaceTemplate tpl = def.Template;
		int radius = Mathf.RoundToInt(tpl.CornerRadius);
		int border = Mathf.RoundToInt(tpl.BorderWidth);

		// 1) 投影 + 底板（先不带边框，否则边框会被底图和文字盖住）
		DrawBox(target, rect, def.FaceTint, def.BorderColor, 0, radius,
			center: true, shadow: true, tpl);

		// 2) 底图
		Texture2D? face = TextureStore.Get(def.FaceImage);
		if (face is not null)
			target.DrawTextureRect(face, rect, false);

		// 3) 字段文字
		DrawFields(target, def, overrides, rect);

		// 4) 边框（画在最上层）
		DrawBox(target, rect, Colors.Transparent, def.BorderColor, border, radius,
			center: false, shadow: false, tpl);
	}

	// ------------------------------------------------------------------ 背面

	public static void DrawBack(CanvasItem target, CardDefinition? def, Rect2 rect)
	{
		Color bg = def?.BackTint ?? new Color("#2b3242");
		Color borderColor = def?.BorderColor ?? new Color("#8fbcbb");
		CardFaceTemplate tpl = def?.Template ?? new CardFaceTemplate();

		int radius = Mathf.RoundToInt(tpl.CornerRadius);
		int border = Mathf.RoundToInt(tpl.BorderWidth);

		DrawBox(target, rect, bg, borderColor, 0, radius, center: true, shadow: true, tpl);

		Texture2D? back = TextureStore.Get(def?.BackImage ?? "");
		if (back is not null)
			target.DrawTextureRect(back, rect, false);
		else
			DrawDefaultBackOrnament(target, rect, borderColor);

		DrawBox(target, rect, Colors.Transparent, borderColor, border, radius,
			center: false, shadow: false, tpl);
	}

	/// <summary>没有卡背图时画一个程序化纹样 —— 免得没图的卡背看起来像渲染失败。</summary>
	private static void DrawDefaultBackOrnament(CanvasItem target, Rect2 rect, Color tint)
	{
		var faint = new Color(tint.R, tint.G, tint.B, 0.40f);

		Rect2 inner = rect.Grow(-rect.Size.X * 0.10f);
		DrawBox(target, inner, Colors.Transparent, faint, 2, 8, center: false, shadow: false, null);

		Vector2 c = rect.GetCenter();
		float r = Mathf.Min(rect.Size.X, rect.Size.Y) * 0.15f;
		Vector2[] diamond =
		{
			c + new Vector2(0f, -r),
			c + new Vector2(r, 0f),
			c + new Vector2(0f, r),
			c + new Vector2(-r, 0f),
		};
		target.DrawColoredPolygon(diamond, faint);
	}

	// ------------------------------------------------------------------ 字段

	private static void DrawFields(
		CanvasItem target,
		CardDefinition def,
		IReadOnlyDictionary<string, string>? overrides,
		Rect2 card)
	{
		CardFaceTemplate tpl = def.Template;
		Font font = Fonts.Ui;
		float pad = card.Size.X * tpl.Padding;

		bool hasTitle = false;

		foreach (CardField field in def.Fields)
		{
			if (field.Slot == FieldSlot.Title)
				hasTitle = true;

			string text = ResolveText(field, overrides);
			if (string.IsNullOrEmpty(text))
				continue;

			Rect2 slot = ResolveSlot(field.Slot, card, pad);
			int fontSize = field.FontSize > 0 ? field.FontSize : DefaultFontSize(tpl, field.Slot);
			Color color = field.Color ?? DefaultColor(tpl, field.Slot);

			if (field.Slot == FieldSlot.Description)
				DrawWrapped(target, slot, text, fontSize, color, font);
			else
				DrawSingleLine(target, slot, text, AlignmentFor(field.Slot), fontSize, color, font);
		}

		// 没定义 Title 字段时用卡名顶替 —— 只写名字的占位卡也能看
		if (!hasTitle && tpl.UseDisplayNameAsTitle && !string.IsNullOrEmpty(def.DisplayName))
		{
			Rect2 slot = ResolveSlot(FieldSlot.Title, card, pad);
			DrawSingleLine(target, slot, def.DisplayName, HorizontalAlignment.Center,
				tpl.TitleFontSize, tpl.TitleColor, font);
		}
	}

	/// <summary>实例级覆盖优先于定义里的值。</summary>
	private static string ResolveText(CardField field, IReadOnlyDictionary<string, string>? overrides)
	{
		if (overrides is not null && overrides.TryGetValue(field.Key, out string? overridden))
		{
			string label = string.IsNullOrEmpty(field.Label) ? field.Key : field.Label;
			return field.ShowLabel ? $"{label} {overridden}" : overridden;
		}

		return field.Render();
	}

	private static int DefaultFontSize(CardFaceTemplate tpl, FieldSlot slot) => slot switch
	{
		FieldSlot.Title => tpl.TitleFontSize,
		FieldSlot.Description => tpl.DescriptionFontSize,
		_ => tpl.FieldFontSize,
	};

	private static Color DefaultColor(CardFaceTemplate tpl, FieldSlot slot) => slot switch
	{
		FieldSlot.Title => tpl.TitleColor,
		FieldSlot.Description => tpl.DescriptionColor,
		_ => tpl.FieldColor,
	};

	private static HorizontalAlignment AlignmentFor(FieldSlot slot) => slot switch
	{
		FieldSlot.TopLeft or FieldSlot.CenterLeft or FieldSlot.BottomLeft or FieldSlot.Description
			=> HorizontalAlignment.Left,
		FieldSlot.TopRight or FieldSlot.CenterRight or FieldSlot.BottomRight
			=> HorizontalAlignment.Right,
		_ => HorizontalAlignment.Center,
	};

	/// <summary>
	/// 槽位 → 卡面内的矩形。比例定位，所以换卡牌尺寸时版式自动等比缩放。
	/// 左右两列收在 34% 以内、中间列占满内宽，三列不重叠。
	/// </summary>
	private static Rect2 ResolveSlot(FieldSlot slot, Rect2 card, float pad)
	{
		float w = card.Size.X;
		float h = card.Size.Y;
		float ox = card.Position.X;
		float oy = card.Position.Y;

		const float RowHeight = 0.085f;
		float colWidth = (w * 0.34f) - pad;
		float rightX = w * 0.66f;

		Rect2 At(float x, float y, float sw, float sh) => new(ox + x, oy + y, sw, sh);

		return slot switch
		{
			FieldSlot.Title => At(pad, h * 0.040f, w - (2f * pad), h * 0.110f),
			FieldSlot.TypeLine => At(pad, h * 0.150f, w - (2f * pad), h * 0.058f),

			FieldSlot.TopLeft => At(pad, h * 0.235f, colWidth, h * RowHeight),
			FieldSlot.TopRight => At(rightX, h * 0.235f, colWidth, h * RowHeight),

			FieldSlot.CenterLeft => At(pad, h * 0.445f, colWidth, h * RowHeight),
			FieldSlot.Center => At(pad, h * 0.445f, w - (2f * pad), h * RowHeight),
			FieldSlot.CenterRight => At(rightX, h * 0.445f, colWidth, h * RowHeight),

			FieldSlot.BottomLeft => At(pad, h * 0.655f, colWidth, h * RowHeight),
			FieldSlot.BottomCenter => At(pad, h * 0.655f, w - (2f * pad), h * RowHeight),
			FieldSlot.BottomRight => At(rightX, h * 0.655f, colWidth, h * RowHeight),

			FieldSlot.Description => At(pad, h * 0.760f, w - (2f * pad), h * 0.195f),

			_ => At(pad, h * 0.445f, w - (2f * pad), h * RowHeight),
		};
	}

	// ------------------------------------------------------------------ 文字

	/// <summary>
	/// 单行文字。
	/// 坑：<c>DrawString</c> 的 <c>pos</c> 是<b>基线</b>而不是左上角，
	/// 所以垂直居中得自己算 ascent/descent。
	/// </summary>
	private static void DrawSingleLine(
		CanvasItem target, Rect2 slot, string text,
		HorizontalAlignment align, int fontSize, Color color, Font font)
	{
		float ascent = font.GetAscent(fontSize);
		float descent = font.GetDescent(fontSize);
		float lineHeight = ascent + descent;
		float baselineY = slot.Position.Y + ((slot.Size.Y - lineHeight) * 0.5f) + ascent;

		string clipped = Ellipsize(font, text, fontSize, slot.Size.X);
		if (string.IsNullOrEmpty(clipped))
			return;

		target.DrawString(font, new Vector2(slot.Position.X, baselineY), clipped,
			align, slot.Size.X, fontSize, color);
	}

	/// <summary>多行自动换行（描述区）。行数按槽位高度截断，防止溢出卡面。</summary>
	private static void DrawWrapped(
		CanvasItem target, Rect2 slot, string text, int fontSize, Color color, Font font)
	{
		float ascent = font.GetAscent(fontSize);
		float descent = font.GetDescent(fontSize);
		float lineHeight = Mathf.Max(ascent + descent, 1f);
		int maxLines = Mathf.Max(Mathf.FloorToInt(slot.Size.Y / lineHeight), 1);
		float baselineY = slot.Position.Y + ascent;

		target.DrawMultilineString(font, new Vector2(slot.Position.X, baselineY), text,
			HorizontalAlignment.Left, slot.Size.X, fontSize, maxLines, color, BreakFlags);
	}

	/// <summary>
	/// 尾部省略号截断。固定槽位里长文本会溢出卡面，Godot 的 DrawString 不做截断。
	/// 二分查找最长的可容纳前缀，复杂度 O(n log n)。
	/// </summary>
	private static string Ellipsize(Font font, string text, int fontSize, float maxWidth)
	{
		if (maxWidth <= 1f || string.IsNullOrEmpty(text))
			return text;

		if (Measure(font, text, fontSize) <= maxWidth)
			return text;

		const string EllipsisChar = "…";
		float ellipsisWidth = Measure(font, EllipsisChar, fontSize);
		if (ellipsisWidth > maxWidth)
			return "";

		int lo = 0;
		int hi = text.Length;
		while (lo < hi)
		{
			int mid = (lo + hi + 1) / 2;
			if (Measure(font, text[..mid], fontSize) + ellipsisWidth <= maxWidth)
				lo = mid;
			else
				hi = mid - 1;
		}

		return text[..lo] + EllipsisChar;
	}

	private static float Measure(Font font, string text, int fontSize)
		=> font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;

	// ------------------------------------------------------------------ 样式盒

	/// <summary>
	/// 画一个圆角矩形。<paramref name="center"/> 控制是否填充内部，
	/// <paramref name="shadow"/> 控制是否投影 —— 分两次调用就能让边框压在文字上面。
	/// </summary>
	private static void DrawBox(
		CanvasItem target, Rect2 rect, Color bg, Color borderColor,
		int borderWidth, int radius, bool center, bool shadow, CardFaceTemplate? tpl)
	{
		Box.BgColor = bg;
		Box.BorderColor = borderColor;
		Box.SetBorderWidthAll(borderWidth);
		Box.SetCornerRadiusAll(radius);
		Box.DrawCenter = center;

		if (shadow && tpl is not null)
		{
			Box.ShadowColor = tpl.ShadowColor;
			Box.ShadowSize = tpl.ShadowSize;
			Box.ShadowOffset = tpl.ShadowOffset;
		}
		else
		{
			Box.ShadowColor = Colors.Transparent;
			Box.ShadowSize = 0;
			Box.ShadowOffset = Vector2.Zero;
		}

		target.DrawStyleBox(Box, rect);
	}
}
