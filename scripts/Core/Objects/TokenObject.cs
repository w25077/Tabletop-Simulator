using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// Token —— 不翻面的小标记：伤害指示物、资源、状态标记。
/// 与卡牌的区别：没有卡背、没有字段列表，内容就是一段文字或一张图。
/// </summary>
public partial class TokenObject : TabletopObject
{
	/// <summary>
	/// 描边的圆角，方形 Token 用它贴合形状。
	///
	/// <b>值住在 <see cref="TokenFaceRenderer"/> 里</b>：编辑器预览也要用它，
	/// 而"预览的圆角与桌上的圆角"是同一个数才谈得上一致。
	/// </summary>
	private const float SquareCornerRadius = TokenFaceRenderer.SquareCornerRadius;

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

	/// <summary>
	/// 画自己。<b>真正的画法在 <see cref="TokenFaceRenderer"/></b> ——
	/// 抽出去是为了让 M5 的编辑器预览与桌上这个 Token 用同一份代码，
	/// 而不是各画各的（那迟早会长得不一样，而"预览好看、桌上不一样"
	/// 是最让人不信任编辑器的症状）。
	/// </summary>
	protected override void DrawContent() => TokenFaceRenderer.Draw(this, Definition, LocalRect, TextOverride);

	/// <summary>圆形 Token 的描边改成圆环，方形走基类的圆角矩形。</summary>
	protected override void DrawOutlineShape(Rect2 rect, Color color, float width)
		=> TokenFaceRenderer.DrawOutline(this, Definition, rect, color, width);

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
