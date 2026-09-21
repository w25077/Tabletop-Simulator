using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// Token 预览：与桌上的 Token 用<b>同一个</b> <see cref="TokenFaceRenderer"/>。
///
/// 与 <see cref="CardPreview"/> 同一个套路（见那边的说明）：预览不是"另画一遍"，
/// 而是换个 <c>CanvasItem</c> 再画一遍，于是"预览好看、桌上不一样"不可能发生。
///
/// 四种形状并排画出来：形状是 Token 最关键的一眼特征
/// （"这个当伤害指示物"就是选它长什么样），只画当前那一个的话，
/// 换形状要反复点下拉才看得出区别。
/// </summary>
public partial class TokenPreview : Control
{
	private TokenDefinition? _definition;

	public bool HasDefinition => _definition is not null;

	/// <summary><c>_Draw</c> 被调用了几次（自检用：把"没画"和"画了但看不见"分开）。</summary>
	public int DrawCount { get; private set; }

	/// <summary>当前形状那个格子的屏幕矩形（自检按它取样像素）。</summary>
	public Rect2 CurrentRectOnScreen => ToScreen(CellRect(CurrentSlot));

	/// <summary>四种形状格里"当前那一档"的下标。</summary>
	private int CurrentSlot => _definition is null ? 0 : (int)_definition.Shape;

	public void SetDefinition(TokenDefinition? definition)
	{
		_definition = definition;
		QueueRedraw();
	}

	/// <summary>布局还没落定时的默认面积（与 CardPreview 同样的处置）。</summary>
	private static readonly Vector2 FallbackArea = new(520f, 170f);

	private Vector2 UsableArea()
	{
		Vector2 size = Size;
		return size.X >= 200f && size.Y >= 120f
			? size
			: new Vector2(Mathf.Max(size.X, FallbackArea.X), Mathf.Max(size.Y, FallbackArea.Y));
	}

	/// <summary>四种形状等宽并排，每个格子里画一个直径约为格高 80% 的 Token。</summary>
	private Rect2 CellRect(int slot)
	{
		Vector2 area = UsableArea();
		float cellWidth = area.X / 4f;
		float side = Mathf.Max(Mathf.Min(cellWidth, area.Y) * 0.8f, 20f);
		float y = (area.Y - side) * 0.5f;

		return new Rect2(
			(slot * cellWidth) + ((cellWidth - side) * 0.5f),
			y,
			side,
			side);
	}

	private Rect2 ToScreen(Rect2 local) => new(GetGlobalRect().Position + local.Position, local.Size);

	public override void _Draw()
	{
		DrawCount++;

		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, 0.18f));

		if (_definition is null)
		{
			DrawString(Fonts.Ui, new Vector2(12f, 24f), "（没有选中指示物）",
				HorizontalAlignment.Left, -1f, 18, new Color("#d8dee9"));
			return;
		}

		// 四种形状全画一遍，当前那一档画描边 —— 一眼看出"现在选的是哪个"
		foreach (TokenShape shape in System.Enum.GetValues<TokenShape>())
		{
			int slot = (int)shape;
			Rect2 cell = CellRect(slot);

			// 用一个临时定义改形状，而不是给渲染器加参数：
			// 参数化会让"预览的形状"与"物件的形状"有两条路径。
			//
			// <b>用 <c>Clone()</c> 而不是在这里逐个属性抄一遍。</b>
			// 抄一份的话，Token 以后加字段（比如"旋转 15°"）会<b>静默漏掉</b> ——
			// 预览照旧画，只是永远不反映那个新字段，而没有任何地方会报错。
			TokenDefinition probe = _definition.Clone();
			probe.Shape = shape;

			// 渲染器要的是"以中心为原点"的矩形，而格子是左上角原点。
			//
			// <b>这里只能平移一次。</b>第一版写的是
			// <c>DrawSetTransform(cell.Position + cell.Size/2, …)</c> 配一个以原点为中心的
			// <c>centered</c> 矩形 —— 两次平移叠在一起，圆心落到了
			// <c>cell 中心 + 半个格子</c> 的地方，而形状框画在 <c>cell</c> 上。
			// 症状就是用户报的那条：<b>"4 个形状的图标位置是歪的，不在框里"</b>。
			//
			// 判据也随之下移到了像素一级（见 DevEditorSim 的 token_preview_* ）：
			// 现在"图标必须落在自己的格子里"是可数的，而不只是"看着挺正"。
			DrawSetTransform(cell.GetCenter(), 0f, Vector2.One);
			TokenFaceRenderer.Draw(this, probe, new Rect2(-cell.Size * 0.5f, cell.Size), "");
			DrawSetTransform(Vector2.Zero, 0f, Vector2.One);

			bool current = shape == _definition.Shape;
			DrawRect(cell.Grow(4f), current ? new Color("#ebcb8b") : new Color(1f, 1f, 1f, 0.15f),
				false, current ? 2f : 1f);
		}
	}

	/// <summary>某个形状格的屏幕矩形（自检按形状取样像素，而不是按"当前那一档"）。</summary>
	internal Rect2 CellOnScreen(TokenShape shape) => ToScreen(CellRect((int)shape));

	/// <summary>格子里的 Token 本身占多大（自检的容差按它算，不写死像素）。</summary>
	internal float CellSide(TokenShape shape) => CellRect((int)shape).Size.X;
}
